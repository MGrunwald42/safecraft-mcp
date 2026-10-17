using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SafeCraftMcp.Registry;

/// <summary>A binary copied to <c>bin\&lt;app&gt;\&lt;sha256&gt;\</c>, the copy sessions run.</summary>
public sealed record FrozenBinary(string Path, string Sha256);

/// <summary>
/// Copies an app's CLI into the gateway's state folder before it runs, so the binary a session
/// runs can't be swapped while it runs and the app's repo is never exposed to the sandbox
///. The hash is taken from the bytes of the copy, not the source, so a source
/// rewritten between hashing and copying can't slip past a pin. Copies are atomic (a unique
/// temp file, then a rename), because two gateways may freeze the same binary at once.
/// </summary>
public static partial class BinaryFreezer
{
    /// <param name="pin">The only SHA-256 allowed, from <c>--pin</c>; null when the app isn't pinned.</param>
    public static FrozenBinary Freeze(string source, AppManifest app, string stateDir, string? pin)
    {
        try
        {
            return FreezeCore(source, app, stateDir, pin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Normal while cargo or update-craft is still writing the exe.
            throw new InvalidOperationException($"Couldn't copy {app.Display}'s binary to the gateway's state folder ({ex.Message}). If it's being built, try again when the build is done.", ex);
        }
    }

    /// <summary>Deletes temp files of freezes that died more than an hour ago (a crash mid-copy).</summary>
    public static void DeleteStaleTemps(string stateDir)
    {
        var bin = Path.Combine(stateDir, "bin");
        if (!Directory.Exists(bin))
            return;
        foreach (var temp in Directory.EnumerateFiles(bin, "*.tmp", SearchOption.AllDirectories))
        {
            try
            {
                // Another gateway may be copying right now; an hour is far longer than any copy.
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > TimeSpan.FromHours(1))
                    File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use; a later start tries again.
            }
        }
    }

    /// <summary>
    /// Deletes frozen copies no session has used for <paramref name="unusedFor"/> (each freeze
    /// stamps its folder), keeping each app's newest. A copy that's still running is locked by
    /// Windows; it stays, read-only as before, and a later start tries again.
    /// </summary>
    /// <returns>The folders deleted.</returns>
    public static IReadOnlyList<string> DeleteUnused(string stateDir, TimeSpan unusedFor)
    {
        var bin = Path.Combine(stateDir, "bin");
        if (!Directory.Exists(bin))
            return [];
        var deleted = new List<string>();
        foreach (var appDir in Directory.EnumerateDirectories(bin))
        {
            var copies = new DirectoryInfo(appDir).EnumerateDirectories()
                .Where(d => HashName().IsMatch(d.Name) && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Skip(1)
                .Where(d => DateTime.UtcNow - d.LastWriteTimeUtc > unusedFor)
                .ToList();
            foreach (var copy in copies)
            {
                var files = copy.GetFiles();
                try
                {
                    foreach (var file in files)
                        file.Attributes = FileAttributes.Normal;
                    copy.Delete(recursive: true);
                    deleted.Add(copy.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    foreach (var file in files)
                        TrySetReadOnly(file);
                }
            }
        }
        return deleted;
    }

    private static void TrySetReadOnly(FileInfo file)
    {
        try
        {
            file.Refresh();
            if (file.Exists)
                file.Attributes = FileAttributes.ReadOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Freeze checks the copy's hash before every use, so a writable copy is still caught.
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashName();

    private static FrozenBinary FreezeCore(string source, AppManifest app, string stateDir, string? pin)
    {
        var appDir = Directory.CreateDirectory(Path.Combine(stateDir, "bin", app.Id)).FullName;
        var temp = Path.Combine(appDir, $"{Guid.NewGuid():N}.tmp");
        try
        {
            string hash;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (pin is not null && pin != hash)
                throw new InvalidOperationException($"{app.Display}'s binary has SHA-256 {hash}, not the pinned {pin}; it won't run.");

            var finalDir = Directory.CreateDirectory(Path.Combine(appDir, hash)).FullName;
            var final = Path.Combine(finalDir, app.Binary);
            if (!File.Exists(final))
            {
                try
                {
                    File.Move(temp, final);
                    File.SetAttributes(final, FileAttributes.ReadOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(final))
                {
                    // Access denied when theirs is already marked read-only.
                    // Another gateway froze the same binary first; theirs is checked below.
                }
            }
            if (SettledSha256Of(final) != hash)
                throw new InvalidOperationException($"The frozen copy {final} doesn't match its folder's hash; delete {finalDir} and try again.");
            MarkUsed(finalDir);
            return new FrozenBinary(final, hash);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    // The folder's write time is the copy's last use, for DeleteUnused.
    private static void MarkUsed(string folder)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another gateway stamping it at the same moment; either stamp will do.
        }
    }

    // Another gateway's rename of the same copy can hold the file for a moment: a sharing
    // violation then, not a real failure. A few short retries ride it out.
    private static string SettledSha256Of(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Sha256Of(path);
            }
            catch (IOException) when (attempt < 20 && File.Exists(path))
            {
                Thread.Sleep(50);
            }
        }
    }

    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
