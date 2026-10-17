using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Tests;

public sealed class RelativePathTests
{
    [Theory]
    [InlineData("out.png", "out.png")]
    [InlineData("photos/cat.jpg", @"photos\cat.jpg")]
    [InlineData(@"a\b\c.psd", @"a\b\c.psd")]
    public void Plain_Relative_Paths_Pass_With_Backslashes(string path, string expected)
    {
        Assert.Equal(expected, RelativePath.Check(path, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape.png")]
    [InlineData(@"a\..\..\x.png")]
    [InlineData("./x.png")]
    [InlineData("a//b.png")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("C:x.png")]
    [InlineData(@"\\server\share\x.png")]
    [InlineData("/etc/passwd")]
    [InlineData("x.png:hidden")]
    [InlineData("x.png::$DATA")]
    [InlineData("CON")]
    [InlineData("nul.png")]
    [InlineData("photos/COM1.jpg")]
    [InlineData("LPT¹")]
    [InlineData("CONIN$")]
    [InlineData("x.png.")]
    [InlineData("x.png ")]
    [InlineData(" x.png")]
    [InlineData("a?.png")]
    [InlineData("a*.png")]
    [InlineData("a\u0001.png")]
    public void Other_Paths_Are_Refused(string path)
    {
        Assert.Null(RelativePath.Check(path, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void A_Name_Has_No_Folders()
    {
        Assert.Equal("a.png", RelativePath.CheckName("a.png", out _));
        Assert.Null(RelativePath.CheckName("photos/a.png", out _));
    }
}
