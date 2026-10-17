using System.Diagnostics;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Tests;

// Runs against the real disk: a temp folder with a "target" folder, a file, and a junction to target.
public sealed class RealPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("safecraft-realpath-").FullName;
    private readonly string _target;
    private readonly string _junction;

    public RealPathTests()
    {
        _target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllText(Path.Combine(_root, "file.txt"), "x");
        _junction = Path.Combine(_root, "link");
        // Junctions need no privilege, unlike symlinks.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", _junction, _target]) { RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    public void Dispose()
    {
        Directory.Delete(_junction);
        Directory.Delete(_root, recursive: true);
    }

    private static string RealTempPath(string path) => RealPath.ResolveFolder(path, out _)!;

    [Fact]
    public void A_Junction_Resolves_To_Its_Target()
    {
        var real = RealPath.ResolveFolder(_junction, out var error);

        Assert.Equal(RealTempPath(_target), real, ignoreCase: true);
        Assert.Empty(error);
    }

    [Fact]
    public void Dot_Segments_And_Trailing_Separators_Resolve()
    {
        Assert.Equal(RealTempPath(_target), RealPath.ResolveFolder(_target + @"\..\target\", out _), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"\??\C:\Windows")]
    [InlineData(@"/\localhost\C$\Windows")]
    [InlineData(@"\/localhost\C$\Windows")]
    [InlineData(@"\\localhost\C$\Windows")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\C:\Windows")]
    [InlineData(@"C:\Windows::$INDEX_ALLOCATION")]
    [InlineData(@"Windows")]
    [InlineData(@"C:Windows")]
    public void Other_Spellings_Of_A_Path_Are_Refused(string path)
    {
        Assert.Null(RealPath.ResolveFolder(path, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void A_Protected_Folder_Behind_A_Junction_Still_Protects_Its_Target()
    {
        // The profile is spelled through the junction ("link"), the grant names the target's .ssh.
        Directory.CreateDirectory(Path.Combine(_target, ".ssh"));
        var host = new HostPaths(_junction, Path.Combine(_junction, "Roaming"), Path.Combine(_junction, "Local"));

        var refused = Assert.Throws<SettingsException>(() =>
            SettingsParser.Parse(["--grant-read", $"keys={Path.Combine(_target, ".ssh")}"], ["photocraft"], host));

        Assert.Contains("protected", refused.Message);
    }

    [Fact]
    public void A_Path_That_Does_Not_Exist_Resolves_Through_Its_Existing_Ancestor()
    {
        var resolved = RealPath.ResolveAsFarAsExists(Path.Combine(_junction, "not", "there"));

        Assert.Equal(Path.Combine(RealTempPath(_target), "not", "there"), resolved, ignoreCase: true);
    }

    [Fact]
    public void A_Missing_Folder_Or_A_File_Is_Refused()
    {
        Assert.Null(RealPath.ResolveFolder(Path.Combine(_root, "missing"), out _));
        Assert.Null(RealPath.ResolveFolder(Path.Combine(_root, "file.txt"), out var error));
        Assert.Contains("isn't a folder", error);
    }
}
