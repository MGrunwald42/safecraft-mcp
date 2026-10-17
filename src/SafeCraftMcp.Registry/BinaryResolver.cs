using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Registry;

public enum BinarySource { Repo, Installed }

/// <summary>A CLI binary found on disk. Nothing here runs it: its facts are read from files.</summary>
public sealed record FoundBinary
{
    public required string Path { get; init; }
    public required BinarySource Source { get; init; }

    /// <summary>Null when the file couldn't be read (see <see cref="Error"/>); such a binary can't run.</summary>
    public string? Sha256 { get; init; }
    public string? Error { get; init; }

    public required long Size { get; init; }
    public required DateTime LastWriteUtc { get; init; }

    /// <summary>From the repo's Cargo.toml or the exe's version resource; null unless it looks like a version.</summary>
    public string? Version { get; init; }

    /// <summary>The repo's HEAD commit (update-craft builds from it; not verified); null unless it's a hash.</summary>
    public string? RepoCommit { get; init; }
}

/// <summary>
/// Finds each app's CLI and hashes it. Hashes are cached per path, size and write time, so
/// <c>crafts_list</c> doesn't re-read a 50 MB exe on every call. The binary itself is never
/// executed outside the sandbox, not even for <c>--version</c>. The version and commit come from
/// files the untrusted repo controls, so only strings shaped like a version or a hash pass: free
/// text there would reach the agent unlabeled.
/// </summary>
public sealed partial class BinaryResolver(GatewaySettings settings)
{
    private readonly ConcurrentDictionary<(string Path, long Size, DateTime Write), string> _hashes = new();

    public FoundBinary? Find(AppManifest app)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var pattern in app.Search)
        {
            var source = pattern.StartsWith("{repos}", StringComparison.Ordinal) ? BinarySource.Repo : BinarySource.Installed;
            var dir = pattern.Replace("{repos}", settings.Repos, StringComparison.Ordinal)
                .Replace("{ProgramFiles}", programFiles, StringComparison.Ordinal)
                .Replace('/', System.IO.Path.DirectorySeparatorChar);
            var file = new FileInfo(System.IO.Path.Combine(dir, app.Binary));
            if (!file.Exists)
                continue;
            var repoDir = source == BinarySource.Repo ? System.IO.Path.Combine(settings.Repos, app.Id) : null;
            string? hash = null, error = null;
            try
            {
                hash = Hash(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Normal while cargo or update-craft writes the exe; report it, don't fail the list.
                error = $"Couldn't read the binary to hash it: {ex.Message}";
            }
            return new FoundBinary
            {
                Path = file.FullName,
                Source = source,
                Sha256 = hash,
                Error = error,
                Size = file.Length,
                LastWriteUtc = file.LastWriteTimeUtc,
                Version = Clean(repoDir is not null ? CargoVersion(repoDir) : InstalledVersion(file.FullName), VersionPattern()),
                RepoCommit = repoDir is not null ? Clean(GitHead(repoDir), CommitPattern()) : null,
            };
        }
        return null;
    }

    private string Hash(FileInfo file) =>
        _hashes.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), _ =>
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        });

    private static string? Clean(string? value, Regex shape) => value is not null && shape.IsMatch(value) ? value : null;

    // Repo files are untrusted and may be locked mid-write (an editor save, git, a virus scan):
    // read at most 64 KB, and treat any failure as "unknown".
    private const int MaxRepoFileBytes = 64 * 1024;

    private static string? ReadRepoFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxRepoFileBytes)
                return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? InstalledVersion(string exe)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\A[0-9A-Za-z.+-]{1,64}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A([0-9a-f]{40}|[0-9a-f]{64})\z")]
    private static partial Regex CommitPattern();

    // The workspace version: '[workspace.package]' (or '[package]') then 'version = "x.y.z"'.
    private static string? CargoVersion(string repoDir)
    {
        if (ReadRepoFile(System.IO.Path.Combine(repoDir, "Cargo.toml")) is not { } toml)
            return null;
        var match = CargoVersionPattern().Match(toml);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^\[(?:workspace\.)?package\][^\[]*?^version\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CargoVersionPattern();

    private static string? GitHead(string repoDir)
    {
        var git = System.IO.Path.Combine(repoDir, ".git");
        if (ReadRepoFile(System.IO.Path.Combine(git, "HEAD"))?.Trim() is not { } head)
            return null;
        if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            return head;
        var reference = head[5..];
        // Only a ref under refs/ is followed; "ref: ../../x" would read an arbitrary file.
        if (!reference.StartsWith("refs/", StringComparison.Ordinal) || reference.Contains("..", StringComparison.Ordinal))
            return null;
        var loose = System.IO.Path.Combine(git, reference.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (ReadRepoFile(loose) is { } commit)
            return commit.Trim();
        return ReadRepoFile(System.IO.Path.Combine(git, "packed-refs"))?
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Split(' '))
            .FirstOrDefault(p => p.Length == 2 && p[1] == reference)?[0];
    }
}
