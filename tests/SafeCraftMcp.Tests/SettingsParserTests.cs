using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Tests;

// A fake host: C:\Users\me. Every plain local folder exists except those named Missing or Nowhere;
// RealPathTests cover the real resolution on disk.
public sealed class SettingsParserTests
{
    private static readonly HostPaths Host = new(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local");
    private static readonly string[] Apps = ["photocraft", "vectorcraft", "pdfcraft", "filmcraft"];

    private static string? FakeFolder(string path, out string error)
    {
        error = "";
        if (!RealPath.IsPlainLocal(path) || !RealPath.IsPlainLocal(Path.GetFullPath(path)))
            error = $"'{path}' isn't an absolute local path";
        else if (path.Contains("Missing") || path.Contains("Nowhere"))
            error = $"'{path}' doesn't exist";
        return error.Length > 0 ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static GatewaySettings Parse(params string[] args) => SettingsParser.Parse(args, Apps, Host, FakeFolder);

    private static SettingsException Refused(params string[] args) => Assert.Throws<SettingsException>(() => Parse(args));

    [Fact]
    public void No_Options_Give_The_Most_Secure_Defaults()
    {
        var settings = Parse();

        Assert.Equal(MinTier.ProcessIsolation, settings.MinTier);
        Assert.Null(settings.Apps);
        Assert.Equal(@"C:\Users\me\Documents\GitHub", settings.Repos);
        Assert.Empty(settings.Pins);
        Assert.Empty(settings.Grants);
        Assert.Empty(settings.ImportExtensions);
        Assert.Empty(settings.ExportExtensions);
        Assert.Empty(settings.MaxFileMb);
        Assert.False(settings.MountGrants);
        Assert.False(settings.KeepWorkspace);
        Assert.False(settings.UpstreamDocs);
        Assert.Equal(2, settings.MaxSessions);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.CallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), settings.ExportTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), settings.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(1), settings.MaxLifetime);
        Assert.Equal(8, settings.MaxResponseMb);
        Assert.Equal(1024, settings.MaxPreviewSide);
        Assert.Equal(8192, settings.MaxMemoryMb);
        Assert.Equal(16, settings.MaxProcesses);
        Assert.Empty(settings.FromOptions);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--network")]
    [InlineData("--learning-mode")]
    [InlineData("stray-argument")]
    public void Unknown_Options_And_Arguments_Are_Refused(string arg)
    {
        Refused(arg);
    }

    [Theory]
    [InlineData("--max-sessions", "0")]
    [InlineData("--max-sessions", "9")]
    [InlineData("--call-timeout", "1801")]
    [InlineData("--export-timeout", "7201")]
    [InlineData("--idle-timeout", "86401")]
    [InlineData("--max-lifetime", "0")]
    [InlineData("--max-response-mb", "65")]
    [InlineData("--max-preview-side", "4096")]
    [InlineData("--max-preview-side", "10")]
    [InlineData("--max-sessions", "two")]
    [InlineData("--max-memory-mb", "100")]
    [InlineData("--max-memory-mb", "200000")]
    [InlineData("--max-processes", "0")]
    [InlineData("--max-processes", "257")]
    public void Limits_Outside_Their_Range_Are_Refused(string option, string value)
    {
        Refused(option, value);
    }

    [Fact]
    public void Limits_Within_The_Caps_Are_Taken_And_Reported_As_Options()
    {
        var settings = Parse("--max-sessions", "8", "--call-timeout", "120", "--max-preview-side", "2048");

        Assert.Equal(8, settings.MaxSessions);
        Assert.Equal(TimeSpan.FromSeconds(120), settings.CallTimeout);
        Assert.Equal(2048, settings.MaxPreviewSide);
        Assert.Equal(["call-timeout", "max-preview-side", "max-sessions"], settings.FromOptions.Order());
    }

    [Theory]
    [InlineData("appcontainer", MinTier.AppContainer)]
    [InlineData("ProcessIsolation", MinTier.ProcessIsolation)]
    public void Min_Tier_Is_Parsed(string value, MinTier expected)
    {
        Assert.Equal(expected, Parse("--min-tier", value).MinTier);
    }

    [Fact]
    public void Min_Tier_Must_Be_A_Known_Tier()
    {
        Refused("--min-tier", "none");
    }

    [Fact]
    public void Apps_Must_Be_Known_And_Named_Once()
    {
        Assert.Equal(["photocraft", "pdfcraft"], Parse("--apps", "photocraft, PdfCraft").Apps);
        Refused("--apps", "photoshop");
        Refused("--apps", "photocraft,photocraft");
        Refused("--apps", ",");
    }

    [Fact]
    public void A_Pin_Is_A_Sha256_Per_Known_App()
    {
        var hash = new string('A', 64);

        Assert.Equal(new string('a', 64), Parse("--pin", $"photocraft={hash}").Pins["photocraft"]);
        Refused("--pin", "photocraft=abc");
        Refused("--pin", $"photoshop={hash}");
        Refused("--pin", $"photocraft={hash}", "--pin", $"photocraft={hash}");
        Refused("--pin", hash);
    }

    [Fact]
    public void Grants_Need_An_Existing_Absolute_Folder_And_A_Unique_Name()
    {
        var settings = Parse("--grant-read", @"in=C:\In", "--grant-write", @"out=C:\Out\");

        Assert.Equal([new Grant("in", @"C:\In", GrantAccess.Read), new Grant("out", @"C:\Out", GrantAccess.Write)], settings.Grants);
        Refused("--grant-read", @"in=C:\Missing");
        Refused("--grant-read", @"in=relative\dir");
        Refused("--grant-read", @"in=\\server\share");
        Refused("--grant-read", @"in=\??\C:\In");
        Refused("--grant-read", @"in=/\localhost\C$\In");
        Refused("--grant-read", @"in=\\?\C:\In");
        Refused("--grant-read", @"in=C:\In::$DATA");
        Refused("--grant-read", @"In=C:\In");
        Refused("--grant-read", @"in=C:\In", "--grant-write", @"in=C:\Out");
        Refused("--grant-write", @"C:\Out");
    }

    [Theory]
    [InlineData(@"C:\Users\me\.ssh")]
    [InlineData(@"C:\Users\me\.claude\projects")]
    [InlineData(@"C:\Users\me\AppData\Roaming\Claude")]
    [InlineData(@"C:\Users\me\Documents\GitHub\photocraft")]
    [InlineData(@"C:\Users\me\AppData\Local\safecraft-mcp\sessions")]
    [InlineData(@"C:\Users\me\AppData\Local\Packages\Claude_abc\LocalCache")]
    [InlineData(@"C:\Users\me\.claude.json")]
    [InlineData(@"C:\Users\me\.gnupg")]
    [InlineData(@"C:\Users\me\Pictures\..\Documents\GitHub\x")]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"C:\")]
    public void A_Grant_Can_Not_Be_Or_Contain_A_Protected_Folder(string dir)
    {
        var refused = Refused("--grant-write", $"out={dir}");

        Assert.Contains("protected", refused.Message);
    }

    [Fact]
    public void A_Grant_Can_Not_Overlap_The_Repos_Folder_From_The_Option()
    {
        var refused = Refused("--repos", @"D:\Code", "--grant-read", @"src=D:\Code\photocraft");

        Assert.Contains("protected", refused.Message);
    }

    [Fact]
    public void A_Grant_Next_To_A_Protected_Folder_Is_Fine()
    {
        // ".sshx" starts with ".ssh" but isn't inside it.
        Assert.Single(Parse("--grant-read", @"in=C:\Users\me\.sshx").Grants);
    }

    [Fact]
    public void Response_Files_Are_Not_Read()
    {
        // "@opts.rsp" would load settings from a file in the client's working directory.
        var file = Path.Combine(Path.GetTempPath(), $"safecraft-{Guid.NewGuid():N}.rsp");
        File.WriteAllText(file, "--min-tier appcontainer");
        try
        {
            Refused("@" + file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Extension_Lists_Replace_Per_App_And_Never_Contain_Refused_Types()
    {
        var settings = Parse("--import-ext", "photocraft=png, .JPG", "--export-ext", "pdfcraft=pdf");

        Assert.Equal(["png", "jpg"], settings.ImportExtensions["photocraft"]);
        Assert.Equal(["pdf"], settings.ExportExtensions["pdfcraft"]);
        Refused("--export-ext", "photocraft=png,exe");
        Refused("--import-ext", "pdfcraft=pfx");
        Refused("--export-ext", "photocraft=LNK");
        Refused("--import-ext", "photocraft=p*g");
        Refused("--import-ext", "photocraft=");
        Refused("--export-ext", "photocraft=scf");
        Refused("--export-ext", "photocraft=pif");
        Refused("--import-ext", "pdfcraft=kdbx");
    }

    [Fact]
    public void Max_File_Size_Is_Per_App_Up_To_The_Hard_Cap()
    {
        Assert.Equal(100, Parse("--max-file-mb", "photocraft=100").MaxFileMb["photocraft"]);
        Refused("--max-file-mb", "photocraft=0");
        Refused("--max-file-mb", $"photocraft={SettingsParser.MaxFileMbCap + 1}");
    }

    [Fact]
    public void Switches_Relax_Only_When_Given()
    {
        var settings = Parse("--mount-grants", "--keep-workspace", "--upstream-docs");

        Assert.True(settings.MountGrants);
        Assert.True(settings.KeepWorkspace);
        Assert.True(settings.UpstreamDocs);
    }

    [Fact]
    public void Repos_Must_Be_An_Existing_Absolute_Folder()
    {
        Assert.Equal(@"D:\Repos", Parse("--repos", @"D:\Repos\").Repos);
        Refused("--repos", "repos");
        Refused("--repos", @"C:\Nowhere");
    }

    [Fact]
    public void Every_Problem_Is_Reported_At_Once()
    {
        var refused = Refused("--max-sessions", "99", "--apps", "nope", "--pin", "photocraft=1");

        Assert.Equal(3, refused.Errors.Count);
    }

    [Fact]
    public void Never_Allowed_Types_Include_Executables_Scripts_And_Keys()
    {
        foreach (var ext in new[] { "exe", "dll", "bat", "cmd", "ps1", "js", "vbs", "lnk", "url", "scr", "msi", "p12", "pfx", "pem", "key", "cer", "der" })
            Assert.Contains(ext, FileTypes.NeverAllowed);
    }
}
