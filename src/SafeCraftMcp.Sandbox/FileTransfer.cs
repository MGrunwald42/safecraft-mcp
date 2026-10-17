using SafeCraftMcp.Settings;
using Microsoft.Win32.SafeHandles;

namespace SafeCraftMcp.Sandbox;

/// <summary>What a copy may carry: the allowed extensions, the size cap, the folders it must never touch.</summary>
public sealed record FileRules(IReadOnlyCollection<string> Extensions, long MaxBytes, IReadOnlyList<string> ProtectedPaths);

/// <summary>A refused or failed copy; the message says why.</summary>
public sealed class FileTransferException(string message) : Exception(message);

/// <summary>
/// Copies one file between a grant folder and a session workspace, done by the
/// trusted gateway with the user's rights. The workspace is the untrusted app's, so every check
/// is made on the opened handles, not on path strings: where the source handle really leads (no
/// link out of its root, no protected folder), how many names it has (no hard link to a file
/// elsewhere), and where the new file really landed. A file swapped for a link between a check
/// and the copy can't redirect it. No clobbering, a size cap enforced while copying, and only the
/// app's extensions for the direction; never-allowed types are refused whatever the list says.
/// </summary>
public static class FileTransfer
{
    /// <param name="sourceRoot">The real path of the folder the file must come from.</param>
    /// <param name="destinationRoot">The real path of the folder the file goes to.</param>
    /// <param name="budgetBytes">How much the destination may still take (the workspace total cap); null for no limit.</param>
    /// <returns>The new file's real path and its size.</returns>
    public static (string Path, long Bytes) Copy(
        string sourceRoot, string relativeSource, string destinationRoot, string? name, FileRules rules, long? budgetBytes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return CopyCore(sourceRoot, relativeSource, destinationRoot, name, rules, budgetBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk full, a quota, a file vanishing mid-copy: a refusal the agent can read.
            throw new FileTransferException($"The copy failed: {ex.Message}");
        }
    }

    private static (string Path, long Bytes) CopyCore(
        string sourceRoot, string relativeSource, string destinationRoot, string? name, FileRules rules, long? budgetBytes,
        CancellationToken cancellationToken)
    {
        var relative = RelativePath.Check(relativeSource, out var error) ?? throw new FileTransferException(error);
        var fileName = RelativePath.CheckName(name ?? Path.GetFileName(relative), out error) ?? throw new FileTransferException(error);
        CheckExtension(relative, rules);
        CheckExtension(fileName, rules);

        using var source = OpenSource(Path.Combine(sourceRoot, relative), sourceRoot, rules);
        var length = RandomAccess.GetLength(source);
        if (length > rules.MaxBytes)
            throw new FileTransferException($"'{relative}' is {Megabytes(length)} MB, over the {Megabytes(rules.MaxBytes)} MB cap.");
        if (budgetBytes is { } budget && length > budget)
            throw new FileTransferException($"'{relative}' ({Megabytes(length)} MB) doesn't fit: the workspace has {Megabytes(Math.Max(budget, 0))} MB left.");

        var target = Path.Combine(destinationRoot, fileName);
        // A link planted under the new name (a dangling file symlink isn't "existing") is refused
        // before anything is created through it.
        if (new FileInfo(target).LinkTarget is not null)
            throw new FileTransferException($"'{fileName}' already exists there as a link; files are never overwritten. Pick another name.");
        SafeFileHandle destination;
        try
        {
            destination = File.OpenHandle(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Path.Exists(target))
        {
            // A folder or a link of that name throws access denied rather than "exists".
            throw new FileTransferException($"'{fileName}' already exists there; files are never overwritten. Pick another name.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileTransferException($"Couldn't create '{fileName}': {ex.Message}");
        }
        var written = false;
        string? landed = null;
        try
        {
            using (destination)
            {
                landed = RealPath.OfHandle(destination);
                if (landed is null || !IsDirectlyIn(landed, destinationRoot))
                    throw new FileTransferException($"'{fileName}' would land outside the destination folder.");
                using var input = new FileStream(source, FileAccess.Read);
                using var output = new FileStream(destination, FileAccess.Write);
                var bytes = CopyCapped(input, output, rules.MaxBytes, relative, cancellationToken);
                // A failing final write shows up here, while the file can still be removed.
                output.Flush(flushToDisk: true);
                written = true;
                return (landed, bytes);
            }
        }
        finally
        {
            // Delete where the file really is, which is the target unless a link redirected it.
            if (!written)
                TryDelete(landed ?? target);
        }
    }

    private static SafeFileHandle OpenSource(string path, string root, FileRules rules)
    {
        SafeFileHandle handle;
        try
        {
            // No write or delete sharing: the file can't change or move while it's copied.
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileTransferException($"There's no file '{Path.GetRelativePath(root, path)}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileTransferException($"Couldn't open '{Path.GetRelativePath(root, path)}' (a folder, or in use): {ex.Message}");
        }
        try
        {
            var real = RealPath.OfHandle(handle);
            if (real is null || !HostPaths.PathIsWithin(real, root) || real.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new FileTransferException($"'{Path.GetRelativePath(root, path)}' leads outside its folder (a link?); refused.");
            if (HostPaths.Overlaps(real, rules.ProtectedPaths))
                throw new FileTransferException($"'{Path.GetRelativePath(root, path)}' is in a protected folder; refused.");
            if (RealPath.LinkCount(handle) > 1)
                throw new FileTransferException($"'{Path.GetRelativePath(root, path)}' is a hard link to another file; refused.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void CheckExtension(string path, FileRules rules)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0 || FileTypes.NeverAllowed.Contains(extension) || !rules.Extensions.Contains(extension))
            throw new FileTransferException(
                $"'{Path.GetFileName(path)}': .{extension} isn't allowed here. Allowed: {string.Join(", ", rules.Extensions.Order())}.");
    }

    private static long CopyCapped(Stream input, Stream output, long maxBytes, string relative, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 20];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > maxBytes)
                throw new FileTransferException($"'{relative}' grew past the {Megabytes(maxBytes)} MB cap while it was copied.");
            output.Write(buffer, 0, read);
        }
        return total;
    }

    private static bool IsDirectlyIn(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do; the copy is reported as failed either way.
        }
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024)).ToString("0.#");

    /// <summary>Total size of the files under <paramref name="folder"/>, without following links.</summary>
    public static long SizeOf(string folder) =>
        new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
            .Sum(f => f.Length);
}
