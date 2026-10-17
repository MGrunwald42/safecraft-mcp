using System.Text;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Tests;

public sealed class AppRegistryTests
{
    private static AppManifest Manifest(
        string args = """["mcp", "--root", "{workspace}"]""", string import = """["png"]""", string export = """["png"]""",
        string id = "testcraft", string binary = "testcraft-cli.exe", string search = """["{repos}/testcraft/target/release"]""") =>
        AppRegistry.Parse(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {
              "id": "{{id}}", "display": "TestCraft", "repo": "storytold/testcraft", "binary": "{{binary}}",
              "search": {{search}}, "args": {{args}}, "env": {}, "ready": true,
              "callTimeoutSeconds": 60, "import": {{import}}, "export": {{export}}, "maxFileMb": 10
            }
            """)), "test");

    [Fact]
    public void The_Embedded_Manifests_Load_And_Pass_Every_Check()
    {
        var registry = AppRegistry.LoadEmbedded();

        Assert.Equal(["filmcraft", "pdfcraft", "photocraft", "vectorcraft"], registry.Ids);
    }

    [Fact]
    public void Only_Apps_That_Passed_Their_Sandbox_Check_Are_Ready()
    {
        // FilmCraft waits on the user's safety-check decision.
        var ready = AppRegistry.LoadEmbedded().Apps.Where(a => a.Ready).Select(a => a.Id);

        Assert.Equal(["pdfcraft", "photocraft", "vectorcraft"], ready);
    }

    [Fact]
    public void VectorCraft_Is_Forced_Headless()
    {
        // Without --headless, vectorcraft-cli mcp tries the desktop app on 127.0.0.1:7979 first.
        var vectorcraft = AppRegistry.LoadEmbedded().Find("vectorcraft")!;

        Assert.Contains("--headless", vectorcraft.Args);
    }

    [Fact]
    public void Every_Manifest_Confines_The_App_To_The_Workspace_Where_The_App_Can()
    {
        var registry = AppRegistry.LoadEmbedded();

        Assert.Equal(["mcp", "--automation-read-root", "{workspace}", "--automation-write-root", "{workspace}"], registry.Find("photocraft")!.Args);
        Assert.Equal(["mcp", "--root", "{workspace}"], registry.Find("pdfcraft")!.Args);
        Assert.Equal(["mcp", "--headless", "--automation-read-root", "{workspace}", "--automation-write-root", "{workspace}"], registry.Find("vectorcraft")!.Args);
    }

    [Theory]
    [InlineData("--bridge")]
    [InlineData("--connect")]
    [InlineData("--control-token-file")]
    [InlineData("--bridge=127.0.0.1:9876")]
    [InlineData("--control-port")]
    [InlineData("--port")]
    [InlineData("--listen")]
    [InlineData("127.0.0.1:7979")]
    [InlineData("localhost:9876")]
    public void Bridge_Flags_And_Addresses_Are_Refused(string flag)
    {
        var app = Manifest($$"""["mcp", "{{flag}}", "x"]""");

        var refused = Assert.Throws<InvalidDataException>(() => new AppRegistry([app]));

        Assert.Contains("desktop app", refused.Message);
    }

    [Theory]
    [InlineData("""["mcp", "-p", "7979"]""")]
    [InlineData("""["mcp", "http://127.0.0.1:7979"]""")]
    [InlineData("""["mcp", "--socket", "x"]""")]
    [InlineData("""["mcp", "--root", "C:/Users"]""")]
    [InlineData("""["serve", "--root", "{workspace}"]""")]
    public void Only_Mcp_Switches_And_Placeholder_Flags_Are_Allowed(string args)
    {
        var app = Manifest(args);

        Assert.Throws<InvalidDataException>(() => new AppRegistry([app]));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("USERPROFILE")]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("lower")]
    public void Env_Keys_The_Gateway_Sets_Are_Refused(string key)
    {
        var json = $$"""
            {"id":"testcraft","display":"T","repo":"a/b","binary":"testcraft-cli.exe","search":["{repos}/t"],"args":["mcp"],
             "env":{"{{key}}":"1"},"ready":true,"callTimeoutSeconds":1,"import":[],"export":[],"maxFileMb":1}
            """;
        var app = AppRegistry.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), "test");

        Assert.Throws<InvalidDataException>(() => new AppRegistry([app]));
    }

    [Fact]
    public void VectorCraft_Without_Headless_Is_Refused()
    {
        var app = Manifest("""["mcp"]""", id: "vectorcraft", binary: "vectorcraft-cli.exe");

        Assert.Throws<InvalidDataException>(() => new AppRegistry([app]));
    }

    [Theory]
    [InlineData("""["target/release"]""", "testcraft-cli.exe", "testcraft")]
    [InlineData("""["{repos}/../x"]""", "testcraft-cli.exe", "testcraft")]
    [InlineData("""["{repos}/testcraft"]""", "..\\\\x-cli.exe", "testcraft")]
    [InlineData("""["{repos}/testcraft"]""", "testcraft.exe", "testcraft")]
    [InlineData("""["{repos}/testcraft"]""", "testcraft-cli.exe", "Test-Craft")]
    public void Search_Folders_Binaries_And_Ids_Must_Be_Plain(string search, string binary, string id)
    {
        var app = Manifest(search: search, binary: binary, id: id);

        Assert.Throws<InvalidDataException>(() => new AppRegistry([app]));
    }

    [Fact]
    public void Unknown_Placeholders_Are_Refused()
    {
        var app = Manifest("""["mcp", "--root", "{home}"]""");

        Assert.Throws<InvalidDataException>(() => new AppRegistry([app]));
    }

    [Theory]
    [InlineData("""["png", "exe"]""", """["png"]""")]
    [InlineData("""["png"]""", """["lnk"]""")]
    [InlineData("""["pfx"]""", """["png"]""")]
    [InlineData("""["PNG"]""", """["png"]""")]
    [InlineData("""["exe\n"]""", """["png"]""")]
    [InlineData("""["png"]""", """["scf"]""")]
    public void Never_Allowed_Or_Malformed_Types_Are_Refused(string import, string export)
    {
        var app = Manifest(import: import, export: export);

        Assert.Throws<InvalidDataException>(() => new AppRegistry([app]));
    }

    [Fact]
    public void Unknown_Manifest_Fields_Are_Refused()
    {
        var json = """{"id":"x","display":"X","repo":"a/b","binary":"x.exe","search":[],"args":[],"ready":true,"callTimeoutSeconds":1,"import":[],"export":[],"maxFileMb":1,"bridge":true}""";

        Assert.Throws<InvalidDataException>(() => AppRegistry.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), "test"));
    }

    [Fact]
    public void No_Embedded_List_Contains_A_Never_Allowed_Type()
    {
        foreach (var app in AppRegistry.LoadEmbedded().Apps)
            Assert.DoesNotContain(app.Import.Concat(app.Export), FileTypes.NeverAllowed.Contains);
    }

    [Fact]
    public void Optional_Fields_Left_Out_Keep_Their_Defaults()
    {
        var json = """{"id":"testcraft","display":"T","repo":"a/b","binary":"testcraft-cli.exe","search":["{repos}/t"],"args":["mcp"],"ready":true,"callTimeoutSeconds":1,"import":[],"export":[],"maxFileMb":1}""";

        var app = AppRegistry.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), "test");

        Assert.Equal("command_list", app.CatalogTool);
        Assert.Empty(app.Env);
        Assert.Empty(app.PreviewTools);
        Assert.Empty(app.LongTools);
        Assert.Null(app.OpenTool);
    }
}
