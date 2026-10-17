using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace SafeCraftMcp.Tests;

public sealed class ToolCatalogTests : IAsyncDisposable
{
    private readonly List<SessionManager> _managers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
            await manager.DisposeAsync();
    }

    private static readonly HostPaths Host = new(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local");

    // A host where MXC offers Process Isolation, without asking the real MXC.
    private static readonly PlatformReport Tier1 = new()
    {
        Supported = true, NativeVersion = "1.0.0", Backends = ["ProcessContainer (BaseContainer)"],
        Tier = Microsoft.Mxc.Sdk.V1.IsolationTier.BaseContainer, NeedsDaclAugmentation = false,
    };

    private static InfoTools Info(GatewaySettings settings, PlatformReport? platform = null) =>
        new(settings, AppRegistry.LoadEmbedded(), new BinaryResolver(settings), Host, [], () => platform ?? Tier1);

    // Every gateway tool, the session and file tools included (no session is opened).
    private ToolCatalog Catalog(GatewaySettings? settings = null)
    {
        settings ??= GatewaySettings.Defaults(Host);
        var info = Info(settings);
        var sessions = new SessionManager(settings, AppRegistry.LoadEmbedded(), new BinaryResolver(settings), Host, NullLoggerFactory.Instance);
        _managers.Add(sessions);
        var workspace = new WorkspaceTools(sessions, settings, Host);
        var sessionTools = new SessionTools(sessions, workspace, settings, NullLogger<SessionTools>.Instance);
        var echo = new GatewayTool
        {
            Name = "echo",
            Title = "Echo",
            Description = "Test tool.",
            Arguments = new Dictionary<string, JsonObject> { ["text"] = new() { ["type"] = "string" } },
            Required = ["text"],
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = call => Task.FromResult(ToolCatalog.Text(JsonValue.Create(call.String("text")))),
        };
        return new ToolCatalog([.. info.Tools(), .. sessionTools.Tools(), .. workspace.Tools(), echo]);
    }

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Every_Tool_Has_A_Title_And_All_Four_Hints()
    {
        Assert.Equal(13, Catalog().Tools.Count);
        foreach (var tool in Catalog().List().Tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Title));
            Assert.NotNull(tool.Annotations?.ReadOnlyHint);
            Assert.NotNull(tool.Annotations?.DestructiveHint);
            Assert.NotNull(tool.Annotations?.IdempotentHint);
            Assert.NotNull(tool.Annotations?.OpenWorldHint);
        }
    }

    [Fact]
    public void Schemas_Refuse_Additional_Properties()
    {
        foreach (var tool in Catalog().List().Tools)
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task An_Unknown_Argument_Is_Invalid_Params()
    {
        var refused = await Assert.ThrowsAsync<McpProtocolException>(() =>
            Catalog().CallAsync("settings_show", Args("""{"min_tier":"appcontainer"}"""), null, CancellationToken.None));

        Assert.Equal(McpErrorCode.InvalidParams, refused.ErrorCode);
        Assert.Contains("min_tier", refused.Message);
    }

    [Fact]
    public async Task A_Missing_Argument_And_An_Unknown_Tool_Are_Invalid_Params()
    {
        var catalog = Catalog();

        var missing = await Assert.ThrowsAsync<McpProtocolException>(() => catalog.CallAsync("echo", null, null, CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<McpProtocolException>(() => catalog.CallAsync("settings_set", null, null, CancellationToken.None));

        Assert.Equal(McpErrorCode.InvalidParams, missing.ErrorCode);
        Assert.Equal(McpErrorCode.InvalidParams, unknown.ErrorCode);
    }

    [Fact]
    public async Task Known_Arguments_Reach_The_Tool()
    {
        var result = await Catalog().CallAsync("echo", Args("""{"text":"hi"}"""), null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("\"hi\"", ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public void No_Gateway_Tool_Takes_A_Setting()
    {
        // L2: tools may take an app, a session, a file name or a grant name, never a setting.
        var settingWords = new[] { "tier", "network", "mount", "env", "flag", "pin", "timeout", "max", "option", "setting" };

        var names = Catalog().Tools.Where(t => t.Name != "echo").SelectMany(t => t.Arguments.Keys);

        Assert.DoesNotContain(names, n => settingWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Settings_Show_Names_The_Source_Of_Each_Value()
    {
        var settings = GatewaySettings.Defaults(Host) with { MaxSessions = 4, FromOptions = new HashSet<string> { "max-sessions" } };

        var shown = Info(settings).SettingsJson();

        Assert.Equal("option --max-sessions", (string?)shown["maxSessions"]!["source"]);
        Assert.Equal(4, (int?)shown["maxSessions"]!["value"]);
        Assert.Equal("default", (string?)shown["minTier"]!["source"]);
        Assert.Equal("fixed (no option)", (string?)shown["network"]!["source"]);
    }

    [Fact]
    public void An_App_Outside_Apps_Or_With_A_Wrong_Pin_Can_Not_Be_Opened()
    {
        // A fake repos folder with a photocraft-cli.exe in it, so the test doesn't depend on a build.
        var repos = Directory.CreateTempSubdirectory("safecraft-tests-").FullName;
        try
        {
            var release = Directory.CreateDirectory(Path.Combine(repos, "photocraft", "target", "release")).FullName;
            File.WriteAllText(Path.Combine(release, "photocraft-cli.exe"), "not really an exe");
            var pinned = GatewaySettings.Defaults(Host) with { Repos = repos, Pins = new Dictionary<string, string> { ["photocraft"] = new('0', 64) } };
            var limited = GatewaySettings.Defaults(Host) with { Repos = repos, Apps = ["pdfcraft"] };

            var pinReason = Reason(pinned, "photocraft");
            var appsReason = Reason(limited, "photocraft");

            Assert.Contains("isn't the pinned", pinReason);
            Assert.Contains("Not in --apps", appsReason);
        }
        finally
        {
            Directory.Delete(repos, recursive: true);
        }

        static string? Reason(GatewaySettings settings, string app)
        {
            return (string?)Info(settings).CraftsList().Single(a => (string?)a!["id"] == app)!["reason"];
        }
    }

    [Fact]
    public void Settings_Show_Gives_The_Effective_File_Rules_Per_App()
    {
        var settings = GatewaySettings.Defaults(Host) with
        {
            ExportExtensions = new Dictionary<string, IReadOnlyList<string>> { ["photocraft"] = ["png"] },
            FromOptions = new HashSet<string> { "export-ext" },
        };

        var files = Info(settings).SettingsJson()["perApp"]!["photocraft"]!;

        Assert.Equal(2048, (int?)files["maxFileMb"]!["value"]);
        Assert.Equal("default (app manifest)", (string?)files["maxFileMb"]!["source"]);
        Assert.Equal(["png"], files["export"]!["value"]!.AsArray().Select(v => (string?)v));
        Assert.Equal("option --export-ext", (string?)files["export"]!["source"]);
        Assert.Contains("psd", files["import"]!["value"]!.AsArray().Select(v => (string?)v));
        Assert.Equal(60, (int?)files["callTimeoutSeconds"]!["value"]);
    }

    [Fact]
    public void Doctor_Reports_A_Refused_Tier_And_Crafts_List_Follows_It()
    {
        var weak = Tier1 with { Tier = Microsoft.Mxc.Sdk.V1.IsolationTier.AppContainerDacl, NeedsDaclAugmentation = true };
        var info = Info(GatewaySettings.Defaults(Host), weak);

        var doctor = info.Doctor();

        Assert.False((bool?)doctor["sessionsAllowed"]);
        Assert.Contains("KB5124010", (string?)doctor["refusal"]);
        Assert.All(info.CraftsList(), app => Assert.False((bool?)app!["canOpen"]));
    }

    [Theory]
    [InlineData("""{"name":"doctor"}""", null)]
    [InlineData("""{"name":"doctor","arguments":{}}""", null)]
    [InlineData("""{"name":"doctor","arguments":null}""", null)]
    [InlineData("""{"name":"doctor","arguments":"x"}""", "'arguments' must be an object")]
    [InlineData("""{"name":"doctor","arguments":[]}""", "'arguments' must be an object")]
    [InlineData("""{"name":"doctor","arguments":1}""", "'arguments' must be an object")]
    [InlineData("""{"arguments":{}}""", "'name' must be a string")]
    [InlineData("""{"name":null}""", "'name' must be a string")]
    [InlineData("""{"name":{}}""", "'name' must be a string")]
    [InlineData("\"x\"", "'params' must be an object")]
    [InlineData("null", "'params' must be an object")]
    public void A_Tools_Call_The_Sdk_Cant_Read_Is_Named(string parameters, string? problem)
    {
        var found = ToolCatalog.MalformedCall(JsonNode.Parse(parameters));

        if (problem is null)
            Assert.Null(found);
        else
            Assert.Contains(problem, found);
    }
}
