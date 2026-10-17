using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.IntegrationTests;

// Files in and out of a real PhotoCraft session through grant folders in temp ("in" read, "out"
// write), as an agent would move them.
[Trait("Category", "Mxc")]
public sealed class WorkspaceTests : IAsyncDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("safecraft-ws-").FullName;
    private readonly string _in;
    private readonly string _out;
    private readonly SessionManager _sessions;
    private readonly ToolCatalog _catalog;

    public WorkspaceTests()
    {
        var host = MxcHost.TestHost(_state);
        _in = RealPath.ResolveFolder(Directory.CreateTempSubdirectory("safecraft-in-").FullName, out _)!;
        _out = RealPath.ResolveFolder(Directory.CreateTempSubdirectory("safecraft-out-").FullName, out _)!;
        var settings = GatewaySettings.Defaults(host) with
        {
            Repos = HostPaths.Current().DefaultRepos,
            Grants = [new Grant("in", _in, GrantAccess.Read), new Grant("out", _out, GrantAccess.Write)],
        };
        var registry = AppRegistry.LoadEmbedded();
        _sessions = new SessionManager(settings, registry, new BinaryResolver(settings), host, NullLoggerFactory.Instance);
        var workspace = new WorkspaceTools(_sessions, settings, host);
        _catalog = new ToolCatalog([.. new SessionTools(_sessions, workspace, settings, NullLogger<SessionTools>.Instance).Tools(), .. workspace.Tools()]);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        foreach (var dir in new[] { _state, _in, _out })
            DeleteFolder(dir);
    }

    private async Task<CallToolResult> Call(string tool, object args) =>
        await _catalog.CallAsync(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!, null, CancellationToken.None);

    private async Task<(string Session, string Workspace)> Open(string? project = null)
    {
        var opened = await Call("session_open", project is null ? new { app = "photocraft" } : new { app = "photocraft", project });
        Assert.False(opened.IsError, Text(opened));
        var info = JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!;
        return ((string)info["session"]!, (string)info["workspace"]!);
    }

    private async Task MakePng(string path)
    {
        // A real PNG, made by PhotoCraft in a throwaway session and exported.
        var (session, _) = await Open();
        await Call("session_call", new { session, tool = "doc_new", arguments = new { width = 64, height = 32 } });
        await Call("session_call", new { session, tool = "doc_save", arguments = new { path = "made.png" } });
        var exported = await Call("workspace_export", new { session, path = "made.png", grant = "out" });
        Assert.False(exported.IsError, Text(exported));
        await Call("session_close", new { session });
        File.Move(Path.Combine(_out, "made.png"), path);
    }

    [MxcFact]
    public async Task A_File_Goes_In_Through_The_App_And_Back_Out()
    {
        await MakePng(Path.Combine(_in, "start.png"));
        var (session, workspace) = await Open();

        var imported = await Call("workspace_import", new { session, grant = "in", path = "start.png" });
        var opened = await Call("session_call", new { session, tool = "doc_open", arguments = new { path = "start.png" } });
        await Call("session_call", new { session, tool = "doc_save", arguments = new { path = "result.png" } });
        var listed = await Call("workspace_list", new { session });
        var exported = await Call("workspace_export", new { session, path = "result.png", grant = "out", name = "final.png" });
        var again = await Call("workspace_export", new { session, path = "result.png", grant = "out", name = "final.png" });

        Assert.False(imported.IsError, Text(imported));
        Assert.False(opened.IsError, Text(opened));
        Assert.StartsWith("[Untrusted output from PhotoCraft", Text(listed));
        Assert.Contains("result.png", Text(listed));
        Assert.False(exported.IsError, Text(exported));
        Assert.True(File.Exists(Path.Combine(_out, "final.png")));
        Assert.True(again.IsError);
        Assert.Contains("never overwritten", Text(again));
        Assert.True(File.Exists(Path.Combine(workspace, "start.png")));
    }

    [MxcFact]
    public async Task A_Project_Is_Imported_And_Opened_At_Session_Start()
    {
        await MakePng(Path.Combine(_in, "project.png"));

        var (session, workspace) = await Open(project: "in/project.png");
        var document = await Call("session_read", new { session, uri = "photocraft://document" });

        Assert.True(File.Exists(Path.Combine(workspace, "project.png")));
        var text = string.Join("\n", document.Content.OfType<EmbeddedResourceBlock>().Select(b => ((TextResourceContents)b.Resource).Text));
        Assert.Contains("\"width\":64", text);
    }

    [MxcFact]
    public async Task A_Project_That_Does_Not_Exist_Opens_No_Session()
    {
        var refused = await Call("session_open", new { app = "photocraft", project = "in/missing.png" });

        Assert.True(refused.IsError);
        Assert.Empty(_sessions.Sessions);
    }

    [MxcFact]
    public async Task Grants_Are_Used_Only_In_Their_Direction_And_Files_Only_Of_The_Apps_Types()
    {
        File.WriteAllText(Path.Combine(_in, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_in, "tool.exe"), "MZ");
        var (session, _) = await Open();

        Assert.Contains("isn't allowed", Text(await Call("workspace_import", new { session, grant = "in", path = "notes.txt" })));
        Assert.Contains("isn't allowed", Text(await Call("workspace_import", new { session, grant = "in", path = "tool.exe" })));
        Assert.Contains("not a read grant", Text(await Call("workspace_import", new { session, grant = "out", path = "x.png" })));
        Assert.Contains("not a write grant", Text(await Call("workspace_export", new { session, path = "x.png", grant = "in" })));
        Assert.Contains("No grant", Text(await Call("workspace_import", new { session, grant = "nope", path = "x.png" })));
        Assert.True((await Call("workspace_import", new { session, grant = "in", path = "../x.png" })).IsError);
    }

    [MxcFact]
    public async Task A_Link_The_App_Leaves_In_Its_Workspace_Exports_Nothing_From_Outside()
    {
        // What a hostile app could leave behind: a junction from its workspace to the user's folder.
        await MakePng(Path.Combine(_in, "private.png"));
        var (session, workspace) = await Open();
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", Path.Combine(workspace, "link"), _in]) { CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();

        var refused = await Call("workspace_export", new { session, path = "link/private.png", grant = "out" });

        Assert.True(refused.IsError);
        Assert.Contains("outside its folder", Text(refused));
        Assert.False(File.Exists(Path.Combine(_out, "private.png")));
    }
}
