using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.IntegrationTests;

// VectorCraft and PdfCraft sessions through the gateway's tools, as an agent would
// drive them: create, draw, preview, save into the workspace, export through a write grant.
[Trait("Category", "Mxc")]
public sealed class MoreAppsTests : IAsyncDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("safecraft-apps-").FullName;
    private readonly string _in;
    private readonly string _out;
    private readonly SessionManager _sessions;
    private readonly ToolCatalog _catalog;

    public MoreAppsTests()
    {
        var host = MxcHost.TestHost(_state);
        _in = RealPath.ResolveFolder(Directory.CreateTempSubdirectory("safecraft-apps-in-").FullName, out _)!;
        _out = RealPath.ResolveFolder(Directory.CreateTempSubdirectory("safecraft-apps-out-").FullName, out _)!;
        var settings = GatewaySettings.Defaults(host) with
        {
            Repos = HostPaths.Current().DefaultRepos,
            Grants = [new Grant("in", _in, GrantAccess.Read), new Grant("out", _out, GrantAccess.Write)],
        };
        var registry = AppRegistry.LoadEmbedded();
        var binaries = new BinaryResolver(settings);
        _sessions = new SessionManager(settings, registry, binaries, host, NullLoggerFactory.Instance);
        var workspace = new WorkspaceTools(_sessions, settings, host);
        _catalog = new ToolCatalog(
        [
            .. new GuideTools(_sessions, registry, binaries, settings).Tools(),
            .. new SessionTools(_sessions, workspace, settings, NullLogger<SessionTools>.Instance).Tools(),
            .. workspace.Tools(),
        ]);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        DeleteFolder(_state);
        Directory.Delete(_in, recursive: true);
        Directory.Delete(_out, recursive: true);
    }

    private async Task<CallToolResult> Call(string tool, object args) =>
        await _catalog.CallAsync(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!, null, CancellationToken.None);

    private static string Resource(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<EmbeddedResourceBlock>().Select(b => ((TextResourceContents)b.Resource).Text));

    private async Task<(string Session, string Workspace)> Open(string app)
    {
        var opened = await Call("session_open", new { app });
        Assert.False(opened.IsError, Text(opened));
        var info = JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!;
        return ((string)info["session"]!, (string)info["workspace"]!);
    }

    private async Task<CallToolResult> App(string session, string tool, object arguments)
    {
        var result = await Call("session_call", new { session, tool, arguments });
        Assert.False(result.IsError, Text(result));
        return result;
    }

    private static int LongSide(CallToolResult preview)
    {
        var png = Assert.Single(preview.Content.OfType<ImageContentBlock>()).DecodedData.Span;
        return Math.Max(BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..]));
    }

    [MxcAppFact("vectorcraft")]
    public async Task VectorCraft_Draws_Previews_Saves_And_Exports()
    {
        var (session, workspace) = await Open("vectorcraft");

        await App(session, "draw_shape", new { shape = "rectangle", x = 72, y = 72, width = 200, height = 120, fill = "#1e88e5", stroke = "none" });
        var preview = await App(session, "render_preview", new { max_side = 4096 });
        await App(session, "save_file", new { path = "art.vectorcraft" });
        await App(session, "export", new { path = "art.svg" });
        var exported = await Call("workspace_export", new { session, path = "art.svg", grant = "out" });
        var escape = await Call("session_call", new { session, tool = "export", arguments = new { path = "../escape.svg" } });

        Assert.Equal(1024, LongSide(preview));
        Assert.True(File.Exists(Path.Combine(workspace, "art.vectorcraft")));
        Assert.False(exported.IsError, Text(exported));
        Assert.StartsWith("<", File.ReadAllText(Path.Combine(_out, "art.svg")).TrimStart());
        Assert.True(escape.IsError);
    }

    [MxcAppFact("pdfcraft")]
    public async Task PdfCraft_Creates_Writes_Previews_Saves_And_Exports()
    {
        var (session, workspace) = await Open("pdfcraft");

        // A page bigger than the cap, so the clamp shows (PdfCraft never upscales).
        await App(session, "doc_create", new { from = "blank", pages = 1, width = 3000, height = 2000 });
        await App(session, "page_add_text", new { doc = 1, page = 1, at = new[] { 72, 72 }, text = "Hello from the sandbox", size = 24 });
        var preview = await App(session, "render_preview", new { doc = 1, max_side = 4096 });
        await App(session, "doc_save", new { doc = 1, path = "out.pdf" });
        var exported = await Call("workspace_export", new { session, path = "out.pdf", grant = "out" });
        var escape = await Call("session_call", new { session, tool = "doc_save", arguments = new { doc = 1, path = "../escape.pdf" } });

        // Asked for 4096, capped at the default --max-preview-side 1024.
        Assert.Equal(1024, LongSide(preview));
        Assert.True(File.Exists(Path.Combine(workspace, "out.pdf")));
        Assert.False(exported.IsError, Text(exported));
        Assert.StartsWith("%PDF", File.ReadAllText(Path.Combine(_out, "out.pdf")));
        Assert.True(escape.IsError);
    }

    [MxcAppFact("pdfcraft")]
    public async Task PdfCraft_Has_No_Signing_Ids_Or_Printers_In_The_Sandbox()
    {
        // The user's certificate store and printers stay out of reach (MXC denies the capability).
        var (session, _) = await Open("pdfcraft");

        // The first block is the gateway's untrusted label; the app's JSON follows it.
        var ids = JsonNode.Parse(((TextContentBlock)(await App(session, "sign_windows_ids", new { })).Content[^1]).Text)!;
        var printers = JsonNode.Parse(((TextContentBlock)(await App(session, "printers", new { })).Content[^1]).Text)!;

        Assert.Equal(0, (int?)ids["count"]);
        Assert.Equal(0, (int?)printers["count"]);
    }

    [MxcAppFact("pdfcraft")]
    public async Task The_PdfCraft_Catalog_Comes_Wrapped_And_Is_Unwrapped()
    {
        // PdfCraft answers command_list with {commands: [...]}; craft_commands accepts both shapes.
        var commands = await Call("craft_commands", new { app = "pdfcraft", filter = "page.rotate" });

        Assert.False(commands.IsError, Text(commands));
        Assert.Contains("\"page.rotate\"", Text(commands));
    }

    [MxcAppFact("vectorcraft")]
    public async Task A_VectorCraft_Project_Opens_At_Session_Start()
    {
        File.WriteAllText(Path.Combine(_in, "logo.svg"),
            """<svg xmlns="http://www.w3.org/2000/svg" width="300" height="200"><rect x="10" y="10" width="100" height="50" fill="#e53935"/></svg>""");

        var opened = await Call("session_open", new { app = "vectorcraft", project = "in/logo.svg" });
        var session = (string)JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!["session"]!;
        var document = Resource(await Call("session_read", new { session, uri = "vectorcraft://document" }));

        Assert.False(opened.IsError, Text(opened));
        Assert.Contains("logo", document);
    }

    [MxcAppFact("pdfcraft")]
    public async Task A_PdfCraft_Project_Opens_At_Session_Start()
    {
        // A PDF made by PdfCraft itself in a first session, then opened as the second one's project.
        var (first, _) = await Open("pdfcraft");
        await App(first, "doc_create", new { from = "blank", pages = 2 });
        await App(first, "doc_save", new { doc = 1, path = "two.pdf" });
        await Call("workspace_export", new { session = first, path = "two.pdf", grant = "out" });
        await Call("session_close", new { session = first });
        File.Move(Path.Combine(_out, "two.pdf"), Path.Combine(_in, "two.pdf"));

        var opened = await Call("session_open", new { app = "pdfcraft", project = "in/two.pdf" });
        var session = (string)JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!["session"]!;
        var document = Resource(await Call("session_read", new { session, uri = "pdfcraft://document" }));

        Assert.False(opened.IsError, Text(opened));
        Assert.Contains("two.pdf", document);
        Assert.Contains("\"pages\":2", document.Replace(" ", ""));
    }
}
