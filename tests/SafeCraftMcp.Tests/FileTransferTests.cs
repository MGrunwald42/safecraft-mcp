using System.Diagnostics;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Tests;

// Real files in temp: a "grant" folder, a "workspace" folder, and a "secret" folder the copies must
// never reach (standing in for .ssh). The workspace's content is what a hostile app could make.
public sealed class FileTransferTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("safecraft-transfer-").FullName;
    private readonly string _grant;
    private readonly string _work;
    private readonly string _secret;
    private readonly FileRules _rules;

    public FileTransferTests()
    {
        _root = RealPath.ResolveFolder(_root, out _)!;
        _grant = Directory.CreateDirectory(Path.Combine(_root, "grant")).FullName;
        _work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        _secret = Directory.CreateDirectory(Path.Combine(_root, "secret")).FullName;
        File.WriteAllText(Path.Combine(_secret, "id_ed25519.png"), "PRIVATE KEY");
        File.WriteAllText(Path.Combine(_grant, "cat.png"), "cat");
        _rules = new FileRules(["png", "jpg"], 1024, [_secret]);
    }

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            Directory.Delete(link);
        Directory.Delete(_root, recursive: true);
    }

    private static void Cmd(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", .. args]) { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private FileTransferException Refused(string from, string path, string to, string? name = null, FileRules? rules = null, long? budget = null) =>
        Assert.Throws<FileTransferException>(() => FileTransfer.Copy(from, path, to, name, rules ?? _rules, budget));

    [Fact]
    public void A_File_Is_Copied_Without_Clobbering()
    {
        var (path, bytes) = FileTransfer.Copy(_grant, "cat.png", _work, null, _rules);

        Assert.Equal(Path.Combine(_work, "cat.png"), path);
        Assert.Equal(3, bytes);
        Assert.Equal("cat", File.ReadAllText(path));
        Assert.Contains("already exists", Refused(_grant, "cat.png", _work).Message);
        Assert.Equal("cat2.png", Path.GetFileName(FileTransfer.Copy(_grant, "cat.png", _work, "cat2.png", _rules).Path));
    }

    [Fact]
    public void A_Junction_In_The_Workspace_Can_Not_Export_A_Secret()
    {
        Cmd("mklink", "/J", Path.Combine(_work, "keys"), _secret);

        Assert.Contains("outside its folder", Refused(_work, "keys/id_ed25519.png", _grant).Message);
        Assert.False(File.Exists(Path.Combine(_grant, "id_ed25519.png")));
    }

    [Fact]
    public void A_Hard_Link_In_The_Workspace_Can_Not_Export_A_Secret()
    {
        Cmd("mklink", "/H", Path.Combine(_work, "innocent.png"), Path.Combine(_secret, "id_ed25519.png"));

        Assert.Contains("hard link", Refused(_work, "innocent.png", _grant).Message);
    }

    [Fact]
    public void A_Junction_Into_A_Protected_Folder_From_A_Grant_Is_Refused()
    {
        // The user's grant folder holds a link to a protected one (the secret is under the root too).
        var rules = _rules with { ProtectedPaths = [_secret] };
        Cmd("mklink", "/J", Path.Combine(_grant, "keys"), _secret);

        Assert.Throws<FileTransferException>(() => FileTransfer.Copy(_grant, "keys/id_ed25519.png", _work, null, rules));
    }

    [Fact]
    public void A_Destination_Name_That_Is_A_Link_Is_Not_Followed()
    {
        // The app pre-creates the import's name as a junction to somewhere else.
        Cmd("mklink", "/J", Path.Combine(_work, "cat.png"), _secret);

        Assert.Contains("already exists", Refused(_grant, "cat.png", _work).Message);
        Assert.Equal("PRIVATE KEY", File.ReadAllText(Path.Combine(_secret, "id_ed25519.png")));
    }

    [Fact]
    public void A_Dangling_Link_Under_The_New_Name_Is_Not_Created_Through()
    {
        // A junction to a folder that doesn't exist yet: it doesn't "exist", but it's a link.
        var target = Path.Combine(_secret, "planted");
        Cmd("mklink", "/J", Path.Combine(_work, "cat.png"), target);

        Assert.Contains("as a link", Refused(_grant, "cat.png", _work).Message);
        Assert.False(Path.Exists(target));
    }

    [Theory]
    [InlineData("cat.exe")]
    [InlineData("cat.lnk")]
    [InlineData("cat.psd")]
    [InlineData("cat")]
    public void Only_The_Allowed_Types_Pass_Both_Ways(string name)
    {
        File.WriteAllText(Path.Combine(_grant, name), "x");

        Assert.Contains("isn't allowed", Refused(_grant, name, _work).Message);
        Assert.Contains("isn't allowed", Refused(_grant, "cat.png", _work, name: name).Message);
    }

    [Fact]
    public void Never_Allowed_Types_Stay_Refused_Even_If_Listed()
    {
        File.WriteAllText(Path.Combine(_grant, "tool.exe"), "MZ");

        Refused(_grant, "tool.exe", _work, rules: _rules with { Extensions = ["exe", "png"] });
    }

    [Fact]
    public void The_Size_Cap_And_The_Workspace_Budget_Hold()
    {
        File.WriteAllBytes(Path.Combine(_grant, "big.png"), new byte[2048]);

        Assert.Contains("cap", Refused(_grant, "big.png", _work).Message);
        Assert.Contains("doesn't fit", Refused(_grant, "cat.png", _work, budget: 2).Message);
    }

    [Theory]
    [InlineData("../grant/cat.png")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("cat.png::$DATA")]
    [InlineData("missing.png")]
    [InlineData(".")]
    public void Bad_Or_Missing_Sources_Are_Refused(string path)
    {
        Refused(_grant, path, _work);
    }

    [Fact]
    public void A_Folder_Is_Not_A_File()
    {
        Directory.CreateDirectory(Path.Combine(_grant, "dir.png"));

        Refused(_grant, "dir.png", _work);
    }

    [Fact]
    public void Workspace_Size_Does_Not_Follow_Links()
    {
        File.WriteAllBytes(Path.Combine(_work, "a.png"), new byte[10]);
        Cmd("mklink", "/J", Path.Combine(_work, "elsewhere"), _secret);

        Assert.Equal(10, FileTransfer.SizeOf(_work));
    }
}
