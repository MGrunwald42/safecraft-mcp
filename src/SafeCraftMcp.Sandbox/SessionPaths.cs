using System.ComponentModel;

namespace SafeCraftMcp.Sandbox;

/// <summary>
/// One session's folders under <c>%TEMP%\safecraft-mcp-sessions\&lt;pid&gt;-&lt;id&gt;</c>: the
/// workspace the app works in and a private profile its user folders point into. The gateway's
/// process id in the name keeps several gateways (Desktop chat and Claude Code at once) apart and
/// lets a later start find what a crashed gateway left behind.
/// </summary>
public sealed record SessionPaths(string Root)
{
    // Written into a session folder kept by --keep-workspace, so no gateway treats it as an orphan.
    private const string KeptMarker = ".kept";

    public string Work => Path.Combine(Root, "work");
    public string Profile => Path.Combine(Root, "profile");
    public string Temp => Path.Combine(Profile, "Temp");
    public string Roaming => Path.Combine(Profile, "AppData", "Roaming");
    public string Local => Path.Combine(Profile, "AppData", "Local");

    /// <param name="sessionsDir">The folder all sessions live in, <c>HostPaths.SessionsDir</c>.</param>
    public static SessionPaths Create(string sessionsDir, string sessionId)
    {
        var paths = new SessionPaths(Path.Combine(sessionsDir, $"{Environment.ProcessId}-{sessionId}"));
        if (Directory.Exists(paths.Root))
            throw new IOException($"Session folder {paths.Root} already exists.");
        foreach (var dir in new[] { paths.Work, paths.Temp, paths.Roaming, paths.Local })
            Directory.CreateDirectory(dir);
        return paths;
    }

    public void MarkKept() => File.WriteAllText(Path.Combine(Root, KeptMarker), "Kept by --keep-workspace; delete this folder by hand when done.\n");

    /// <summary>
    /// Deletes the session folder. The app controls what's in its workspace, so read-only files
    /// and junctions or symlinks are expected: attributes are cleared, and links are removed
    /// without following them (a junction to a user folder must not empty that folder).
    /// </summary>
    public void Delete()
    {
        if (Directory.Exists(Root))
            DeleteTree(new DirectoryInfo(Root));
    }

    private static void DeleteTree(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            entry.Attributes = FileAttributes.Normal | (entry.Attributes & FileAttributes.Directory);
            if (entry is DirectoryInfo sub && entry.LinkTarget is null && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                DeleteTree(sub);
            else
                entry.Delete();
        }
        dir.Attributes = FileAttributes.Directory;
        dir.Delete();
    }

    /// <summary>
    /// Deletes session folders whose gateway process is gone (a crash, a killed client). A folder
    /// of a running process is left alone, also when the id was reused by another program, and so
    /// is a folder kept with <c>--keep-workspace</c>.
    /// </summary>
    public static IReadOnlyList<string> DeleteOrphans(string sessionsDir)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(sessionsDir))
            return deleted;
        foreach (var dir in Directory.EnumerateDirectories(sessionsDir))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name.Split('-')[0], out var pid) || pid == Environment.ProcessId || IsRunning(pid)
                || File.Exists(Path.Combine(dir, KeptMarker)))
                continue;
            try
            {
                new SessionPaths(dir).Delete();
                deleted.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use (a sandbox that hasn't exited yet); the next start tries again.
            }
        }
        return deleted;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // A protected or elevated process (access denied): it's running, and not ours to judge.
            return true;
        }
    }
}
