using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.IntegrationTests;

// Sessions in the real session folder (%TEMP%\safecraft-mcp-sessions), not a test's temp folder:
// a read-write grant below %LOCALAPPDATA% let PhotoCraft create files but not rename them, so
// every crash-safe save failed with "access denied" while the tests, all in temp folders, passed.
// Only the frozen binaries go to a test folder.
[Trait("Category", "Mxc")]
public sealed class SessionFolderTests : IAsyncDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("safecraft-sf-").FullName;
    private readonly SessionManager _sessions;
    private readonly ToolCatalog _catalog;

    public SessionFolderTests()
    {
        var real = HostPaths.Current();
        var host = new HostPaths(real.UserProfile, real.AppData, _state, real.Documents);
        var settings = GatewaySettings.Defaults(host) with { Repos = real.DefaultRepos };
        _sessions = new SessionManager(settings, AppRegistry.LoadEmbedded(), new BinaryResolver(settings), host, NullLoggerFactory.Instance);
        var workspace = new WorkspaceTools(_sessions, settings, host);
        _catalog = new ToolCatalog([.. new SessionTools(_sessions, workspace, settings, NullLogger<SessionTools>.Instance).Tools()]);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        DeleteFolder(_state);
    }

    private async Task<CallToolResult> Call(string tool, object args) =>
        await _catalog.CallAsync(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!, null, CancellationToken.None);

    [MxcAppFact("photocraft")]
    public async Task A_Session_In_The_Real_Session_Folder_Saves_And_Overwrites()
    {
        var opened = await Call("session_open", new { app = "photocraft" });
        Assert.False(opened.IsError, Text(opened));
        var info = JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!;
        var (session, workspace) = ((string)info["session"]!, (string)info["workspace"]!);
        Assert.StartsWith(HostPaths.Current().SessionsDir + Path.DirectorySeparatorChar, workspace, StringComparison.OrdinalIgnoreCase);

        Assert.False((await Call("session_call", new { session, tool = "doc_new", arguments = new { width = 64, height = 64 } })).IsError);
        var first = await Call("session_call", new { session, tool = "doc_save", arguments = new { path = "out.png" } });
        var native = await Call("session_call", new { session, tool = "doc_save", arguments = new { path = "out.pcraft" } });
        // The second save renames its temp file over the first one.
        var again = await Call("session_call", new { session, tool = "doc_save", arguments = new { path = "out.png" } });

        Assert.False(first.IsError, Text(first));
        Assert.False(native.IsError, Text(native));
        Assert.False(again.IsError, Text(again));
        Assert.True(File.Exists(Path.Combine(workspace, "out.png")));
        Assert.True(Path.Exists(Path.Combine(workspace, "out.pcraft")));
        Assert.Equal(["out.pcraft", "out.png"], Directory.EnumerateFileSystemEntries(workspace).Select(Path.GetFileName).Order());

        Assert.False((await Call("session_close", new { session })).IsError);
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace)));
    }
}
