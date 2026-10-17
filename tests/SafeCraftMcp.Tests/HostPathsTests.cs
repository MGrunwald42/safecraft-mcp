using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Tests;

public sealed class HostPathsTests
{
    private static readonly HostPaths Host = new(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", @"D:\OneDrive\Documents");

    [Theory]
    [InlineData(@"C:\Users\me\.ssh", @"C:\Users\me\.ssh", true)]
    [InlineData(@"C:\Users\me\.ssh\id_ed25519", @"C:\Users\me\.ssh", true)]
    [InlineData(@"C:\Users\me\.ssh\", @"C:\Users\me\.ssh", true)]
    [InlineData(@"c:\users\ME\.SSH", @"C:\Users\me\.ssh", true)]
    [InlineData(@"C:\Users\me\.sshx", @"C:\Users\me\.ssh", false)]
    [InlineData(@"C:\Users\me", @"C:\Users\me\.ssh", false)]
    [InlineData(@"C:\anything", @"C:\", true)]
    [InlineData(@"D:\anything", @"C:\", false)]
    public void Path_Is_Within_Compares_Whole_Segments(string path, string root, bool within)
    {
        Assert.Equal(within, HostPaths.PathIsWithin(path, root));
    }

    [Fact]
    public void Overlap_Counts_Both_Directions()
    {
        Assert.True(HostPaths.Overlaps(@"C:\Users\me", Host.Protected));
        Assert.True(HostPaths.Overlaps(@"C:\Users\me\.ssh\keys", Host.Protected));
        Assert.False(HostPaths.Overlaps(@"C:\Users\me\Pictures\SafeCraft-out", Host.Protected));
    }

    [Fact]
    public void The_Documents_Known_Folder_Is_Protected_Too()
    {
        // Under OneDrive the Documents folder moves, and with it the default GitHub folder.
        Assert.True(HostPaths.Overlaps(@"D:\OneDrive\Documents\GitHub\photocraft", Host.Protected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(@"\\server\share\me")]
    public void An_Unusable_Known_Folder_Stops_The_Gateway(string profile)
    {
        Assert.Throws<InvalidOperationException>(() => new HostPaths(profile, @"C:\a", @"C:\b"));
        Assert.Throws<InvalidOperationException>(() => new HostPaths(@"C:\Users\me", @"C:\a", @"C:\b", temp: profile));
    }

    [Fact]
    public void Sessions_Live_In_The_Temp_Folder_And_Are_Protected()
    {
        // A read-write grant below %LOCALAPPDATA% (outside its Temp) can't rename, which breaks
        // every app's crash-safe save, so the sessions stay out of the state folder.
        var withTemp = new HostPaths(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", temp: @"C:\Users\me\AppData\Local\Temp");

        Assert.Equal(@"C:\Users\me\AppData\Local\Temp\safecraft-mcp-sessions", withTemp.SessionsDir);
        Assert.False(HostPaths.PathIsWithin(withTemp.SessionsDir, withTemp.StateDir));
        Assert.Contains(withTemp.SessionsDir, withTemp.Protected);
        Assert.Equal(Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), "safecraft-mcp-sessions"), Host.SessionsDir);
    }
}
