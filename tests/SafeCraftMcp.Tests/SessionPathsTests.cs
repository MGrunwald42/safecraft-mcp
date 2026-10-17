using System.Diagnostics;
using SafeCraftMcp.Sandbox;

namespace SafeCraftMcp.Tests;

public sealed class SessionPathsTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("safecraft-state-").FullName;

    public void Dispose() => Directory.Delete(_state, recursive: true);

    [Fact]
    public void A_Session_Gets_Its_Own_Folders_Named_With_The_Process_Id()
    {
        var paths = SessionPaths.Create(_state, "s1");

        Assert.Equal(Path.Combine(_state, $"{Environment.ProcessId}-s1"), paths.Root);
        Assert.True(Directory.Exists(paths.Work));
        Assert.True(Directory.Exists(paths.Temp));
        Assert.Throws<IOException>(() => SessionPaths.Create(_state, "s1"));

        paths.Delete();

        Assert.False(Directory.Exists(paths.Root));
    }

    [Fact]
    public void Orphans_Of_Gone_Gateways_Are_Deleted_And_Live_Ones_Kept()
    {
        using var finished = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        finished.WaitForExit();
        var gone = Directory.CreateDirectory(Path.Combine(_state, $"{finished.Id}-s1")).FullName;
        var mine = SessionPaths.Create(_state, "s2").Root;
        var unparsable = Directory.CreateDirectory(Path.Combine(_state, "notes")).FullName;

        var deleted = SessionPaths.DeleteOrphans(_state);

        Assert.Equal([gone], deleted);
        Assert.True(Directory.Exists(mine));
        Assert.True(Directory.Exists(unparsable));
    }

    [Fact]
    public void A_Folder_Named_For_A_Protected_Process_Is_Kept()
    {
        // Pid 4 is the System process: asking whether it exited is access denied.
        var system = Directory.CreateDirectory(Path.Combine(_state, "4-s1")).FullName;

        var deleted = SessionPaths.DeleteOrphans(_state);

        Assert.Empty(deleted);
        Assert.True(Directory.Exists(system));
    }

    [Fact]
    public void A_Kept_Workspace_Is_No_Orphan()
    {
        using var finished = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        finished.WaitForExit();
        var kept = new SessionPaths(Directory.CreateDirectory(Path.Combine(_state, $"{finished.Id}-s1")).FullName);
        kept.MarkKept();

        Assert.Empty(SessionPaths.DeleteOrphans(_state));
    }

    [Fact]
    public void Delete_Handles_Read_Only_Files_And_Leaves_A_Junction_Target_Alone()
    {
        // The app controls its workspace: a read-only file, and a junction to a folder outside.
        var outside = Directory.CreateDirectory(Path.Combine(_state, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "user data");
        var paths = SessionPaths.Create(_state, "s3");
        var readOnly = Path.Combine(paths.Work, "locked.png");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", Path.Combine(paths.Work, "link"), outside]) { CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();

        paths.Delete();

        Assert.False(Directory.Exists(paths.Root));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }
}
