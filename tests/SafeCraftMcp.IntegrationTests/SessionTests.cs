using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.IntegrationTests;

// Real sessions: PhotoCraft's CLI in a Tier 1 sandbox, driven through the gateway's tools as an
// agent would. The gateway's state goes to a temp folder per test.
[Trait("Category", "Mxc")]
public sealed class SessionTests : IAsyncDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("safecraft-it-").FullName;
    private readonly HostPaths _host;
    private SessionManager? _sessions;
    private ToolCatalog? _catalog;
    private GatewaySettings? _settings;
    private InfoTools? _info;

    public SessionTests() => _host = MxcHost.TestHost(_state);

    private ToolCatalog Gateway(Func<GatewaySettings, GatewaySettings>? configure = null)
    {
        _settings = (configure ?? (s => s))(GatewaySettings.Defaults(_host) with { Repos = HostPaths.Current().DefaultRepos });
        var registry = AppRegistry.LoadEmbedded();
        var binaries = new BinaryResolver(_settings);
        _sessions = new SessionManager(_settings, registry, binaries, _host, NullLoggerFactory.Instance);
        _info = new InfoTools(_settings, registry, binaries, _host, [], () => TierGate.Describe(TierGate.ProbeRequest(SandboxRequest.UsableDeniedPaths(_settings.ProtectedPaths(_host)))));
        var workspace = new WorkspaceTools(_sessions, _settings, _host);
        var guides = new GuideTools(_sessions, registry, binaries, _settings);
        _catalog = new ToolCatalog(
            [.. _info.Tools(), .. guides.Tools(), .. new SessionTools(_sessions, workspace, _settings, NullLogger<SessionTools>.Instance).Tools(), .. workspace.Tools()]);
        return _catalog;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessions is not null)
            await _sessions.DisposeAsync();
        DeleteFolder(_state);
    }

    private static Dictionary<string, JsonElement> Args(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private async Task<CallToolResult> Call(string tool, object args) =>
        await _catalog!.CallAsync(tool, Args(args), null, CancellationToken.None);

    private async Task<(string Session, string Workspace)> Open()
    {
        var opened = await Call("session_open", new { app = "photocraft" });
        Assert.False(opened.IsError, Text(opened));
        var info = JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!;
        return ((string)info["session"]!, (string)info["workspace"]!);
    }

    private async Task<CallToolResult> App(string session, string tool, object arguments) =>
        await Call("session_call", new { session, tool, arguments });

    private static (int Width, int Height) PngSize(ReadOnlySpan<byte> png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..]));

    [MxcFact]
    public async Task A_Session_Creates_Paints_Previews_Saves_And_Closes()
    {
        Gateway();
        var (session, workspace) = await Open();

        Assert.False((await App(session, "doc_new", new { width = 2000, height = 1000 })).IsError);
        Assert.False((await App(session, "command_run", new { id = "layer.newFillLayer.gradient", @params = new { from = "#ff6000", to = "#1030c0" } })).IsError);
        var preview = await App(session, "render_preview", new { max_side = 4000 });
        var saved = await App(session, "doc_save", new { path = "out.png" });

        var image = Assert.Single(preview.Content.OfType<ImageContentBlock>());
        // Asked for 4000, capped at the default --max-preview-side 1024.
        Assert.Equal((1024, 512), PngSize(image.DecodedData.Span));
        Assert.StartsWith("[Untrusted output from PhotoCraft", Text(preview));
        Assert.False(saved.IsError, Text(saved));
        Assert.True(File.Exists(Path.Combine(workspace, "out.png")));

        var closed = await Call("session_close", new { session });

        Assert.False(closed.IsError);
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace)));
    }

    [MxcFact]
    public async Task The_Preview_Cap_Follows_The_Option()
    {
        Gateway(s => s with { MaxPreviewSide = 256 });
        var (session, _) = await Open();
        await App(session, "doc_new", new { width = 800, height = 400 });

        var preview = await App(session, "render_preview", new { });

        Assert.Equal((256, 128), PngSize(Assert.Single(preview.Content.OfType<ImageContentBlock>()).DecodedData.Span));
    }

    [MxcFact]
    public async Task The_App_Confines_Saves_To_The_Workspace_Too()
    {
        Gateway();
        var (session, workspace) = await Open();
        await App(session, "doc_new", new { width = 10, height = 10 });

        var escape = await App(session, "doc_save", new { path = "../escape.png" });
        var absolute = await App(session, "doc_save", new { path = Path.Combine(_state, "escape.png") });

        Assert.True(escape.IsError);
        Assert.True(absolute.IsError);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(workspace)!, "escape.png")));
        Assert.False(File.Exists(Path.Combine(_state, "escape.png")));
    }

    [MxcFact]
    public async Task The_Session_Limit_Holds()
    {
        Gateway(s => s with { MaxSessions = 1 });
        await Open();

        var second = await Call("session_open", new { app = "photocraft" });

        Assert.True(second.IsError);
        Assert.Contains("--max-sessions", Text(second));
    }

    [MxcFact]
    public async Task An_Idle_Session_Is_Closed_By_The_Sweep()
    {
        Gateway(s => s with { IdleTimeout = TimeSpan.FromSeconds(1) });
        var (session, workspace) = await Open();
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        await _sessions!.SweepAsync();

        Assert.True((await Call("session_tools", new { session })).IsError);
        Assert.False(Directory.Exists(Path.GetDirectoryName(workspace)));
    }

    [MxcFact]
    public async Task A_Wrong_Pin_Refuses_To_Start()
    {
        Gateway(s => s with { Pins = new Dictionary<string, string> { ["photocraft"] = new('0', 64) } });

        var refused = await Call("session_open", new { app = "photocraft" });

        Assert.True(refused.IsError);
        Assert.Contains("pinned", Text(refused));
    }

    [MxcFact]
    public async Task Session_Open_Takes_No_Setting()
    {
        Gateway();

        var refused = await Assert.ThrowsAsync<McpProtocolException>(() => Call("session_open", new { app = "photocraft", tier = "appcontainer" }));

        Assert.Equal(McpErrorCode.InvalidParams, refused.ErrorCode);
    }

    [MxcFact]
    public async Task Settings_Are_The_Same_Before_And_After_A_Session()
    {
        Gateway();
        var before = _info!.SettingsJson().ToJsonString();
        var (session, _) = await Open();
        await App(session, "doc_new", new { width = 10, height = 10 });
        await Call("session_close", new { session });

        Assert.Equal(before, _info.SettingsJson().ToJsonString());
    }

    [MxcFact]
    public async Task The_Apps_Errors_Pass_Through_Labeled()
    {
        Gateway();
        var (session, _) = await Open();

        var unknownArgument = await Assert.ThrowsAsync<McpProtocolException>(() => App(session, "doc_new", new { bogus = 1 }));
        var unknownTool = await App(session, "no_such_tool", new { });
        var foreignResource = await Call("session_read", new { session, uri = "file:///C:/Windows/win.ini" });
        var document = await Call("session_read", new { session, uri = "photocraft://document" });

        Assert.Equal(McpErrorCode.InvalidParams, unknownArgument.ErrorCode);
        Assert.Contains("[Untrusted output from PhotoCraft", unknownArgument.Message);
        Assert.True(unknownTool.IsError);
        Assert.True(foreignResource.IsError);
        Assert.IsType<EmbeddedResourceBlock>(document.Content[1]);
    }

    // Noise on an 8000 x 8000 document takes about 1.5 s on PhotoCraft 0.8.2, a 250 px blur after it about 2 s.
    private async Task<string> SlowDocument()
    {
        var (session, _) = await Open();
        Assert.False((await App(session, "doc_new", new { width = 8000, height = 8000 })).IsError);
        return session;
    }

    private Task<CallToolResult> SlowCall(string session) =>
        App(session, "command_run", new { id = "filter.noise.addNoise", @params = new { } });

    [MxcFact]
    public async Task A_Call_Over_The_Timeout_Is_Cancelled_And_Reported()
    {
        Gateway(s => s with { CallTimeout = TimeSpan.FromSeconds(1), FromOptions = new HashSet<string> { "call-timeout" } });
        var session = await SlowDocument();

        var result = await SlowCall(session);

        Assert.True(result.IsError);
        Assert.Contains("didn't finish within 1 s", Text(result));
        // PhotoCraft finishes the step in the background and keeps serving (after the step: give it time).
        Assert.Contains("still open", Text(result));
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False((await App(session, "doc_inspect", new { })).IsError);
    }

    [MxcFact]
    public async Task Closing_A_Session_During_A_Call_Tells_The_Caller()
    {
        // Closing gives the app 2 s to finish; three noise steps take longer, so it's killed.
        Gateway();
        var session = await SlowDocument();
        var step = new { id = "filter.noise.addNoise", @params = new { } };
        var running = App(session, "command_batch", new { steps = new[] { step, step, step } });
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await Call("session_close", new { session });
        var result = await running;

        Assert.True(result.IsError);
        Assert.Contains("was closed", Text(result));
    }

    // Records progress as it's reported (Progress<T> would post it to the thread pool, out of order).
    private sealed class ProgressLog : IProgress<ProgressNotificationValue>
    {
        private readonly List<ProgressNotificationValue> _values = [];

        public void Report(ProgressNotificationValue value)
        {
            lock (_values)
                _values.Add(value);
        }

        public List<ProgressNotificationValue> Values
        {
            get
            {
                lock (_values)
                    return [.. _values];
            }
        }
    }

    // A clip of duration x fps frames; PhotoCraft's Render Video reports one progress step per frame.
    private async Task<(string Session, string Workspace)> Clip(int width, int height, int duration, int fps)
    {
        var (session, workspace) = await Open();
        Assert.False((await App(session, "doc_new", new { width, height, name = "clip" })).IsError);
        Assert.False((await App(session, "command_run", new { id = "timeline.create", @params = new { duration, fps } })).IsError);
        return (session, workspace);
    }

    private Task<CallToolResult> Render(string session, string dir, IProgress<ProgressNotificationValue> progress, CancellationToken cancellationToken) =>
        _catalog!.CallAsync("session_call",
            Args(new { session, tool = "command_run", arguments = new { id = "file.export.renderVideo", @params = new { dir, format = "png" } } }),
            progress, cancellationToken);

    [MxcFact]
    public async Task The_Apps_Progress_Reaches_The_Caller()
    {
        Gateway();
        var (session, workspace) = await Clip(64, 48, duration: 6, fps: 6);
        var log = new ProgressLog();

        var result = await Render(session, "frames", log, CancellationToken.None);
        // The SDK may hand over the last notification just after the reply.
        for (var wait = 0; wait < 20 && log.Values.LastOrDefault()?.Progress != 6; wait++)
            await Task.Delay(50);

        Assert.False(result.IsError, Text(result));
        Assert.Equal(6, Directory.GetFiles(Path.Combine(workspace, "frames")).Length);
        var values = log.Values;
        Assert.Equal(0, values[0].Progress);
        Assert.Equal(6, values[^1].Progress);
        Assert.All(values, v => Assert.Equal(6, v.Total));
        Assert.True(values.Zip(values.Skip(1)).All(p => p.First.Progress < p.Second.Progress), string.Join(", ", values.Select(v => v.Progress)));
    }

    [MxcFact]
    public async Task A_Caller_Cancelling_Stops_The_App_And_Keeps_The_Session()
    {
        Gateway();
        // 3000 frames (duration counts frames): minutes of work, so only a cancel that reaches the app ends it in time.
        var (session, workspace) = await Clip(1600, 1200, duration: 3000, fps: 30);
        var log = new ProgressLog();
        using var cancel = new CancellationTokenSource();

        var running = Render(session, "cancel", log, cancel.Token);
        for (var wait = 0; wait < 600 && !log.Values.Any(v => v.Progress >= 1); wait++)
            await Task.Delay(50);
        Assert.Contains(log.Values, v => v.Progress >= 1);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        // doc_inspect waits for the app's session lock, so the export's cleanup is done by its reply.
        var inspect = await App(session, "doc_inspect", new { });
        Assert.False(inspect.IsError, Text(inspect));
        // PhotoCraft removes the frames a cancelled export made: the cancel reached the app.
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace, "cancel")));
    }

    [MxcFact]
    public async Task A_Timed_Out_Export_Is_Cancelled_In_The_App()
    {
        Gateway(s => s with { CallTimeout = TimeSpan.FromSeconds(2), FromOptions = new HashSet<string> { "call-timeout" } });
        var (session, workspace) = await Clip(1600, 1200, duration: 3000, fps: 30);

        var result = await Render(session, "late", new ProgressLog(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("didn't finish within 2 s", Text(result));
        Assert.Contains("still open", Text(result));
        // Without the cancel the export would hold the app for minutes and this would time out too.
        Assert.False((await App(session, "doc_inspect", new { })).IsError);
        Assert.Empty(Directory.GetFiles(Path.Combine(workspace, "late")));
    }

    [MxcFact]
    public async Task Mxc_Ends_A_Session_At_Its_Lifetime_Even_Without_The_Sweep()
    {
        Gateway(s => s with { MaxLifetime = TimeSpan.FromSeconds(2) });
        var (session, _) = await Open();
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.True(_sessions!.Get(session).Child.HasExited);
    }

    [MxcFact]
    public async Task A_Failed_Read_Is_Labeled()
    {
        Gateway();
        var (session, _) = await Open();

        var refused = await Assert.ThrowsAsync<McpProtocolException>(() => Call("session_read", new { session, uri = "photocraft://nonexistent" }));

        Assert.Contains("[Untrusted output from PhotoCraft", refused.Message);
    }

    [MxcFact]
    public async Task The_Tool_Names_In_The_Open_Result_Follow_The_Label()
    {
        Gateway();

        var opened = await Call("session_open", new { app = "photocraft" });

        var texts = opened.Content.OfType<TextContentBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain("command_run", texts[0]);
        Assert.StartsWith("[Untrusted output from PhotoCraft", texts[1]);
        Assert.Contains("command_run", texts[2]);
    }

    [MxcFact]
    public async Task The_Command_Catalog_Is_Fetched_Once_And_Then_Cached()
    {
        // One session slot: after the first fetch it's taken, so the second call must come from the cache.
        Gateway(s => s with { MaxSessions = 1 });

        var first = await Call("craft_commands", new { app = "photocraft", filter = "gaussianBlur" });
        Assert.Empty(_sessions!.Sessions);
        await Open();
        var second = await Call("craft_commands", new { app = "photocraft", filter = "gaussianBlur" });

        Assert.False(first.IsError, Text(first));
        Assert.StartsWith("[Untrusted output from PhotoCraft", Text(first));
        Assert.Contains("filter.blur.gaussianBlur", Text(first));
        Assert.Equal(Text(first), Text(second));
    }

    [MxcFact]
    public async Task A_Session_Runs_In_A_Job_That_Ends_It_When_Closed()
    {
        // Closing the job handle is what Windows does when the gateway process ends, a crash included.
        Gateway(s => s with { MaxMemoryMb = 1024, MaxProcesses = 5 });
        var (id, _) = await Open();
        var session = _sessions!.Get(id);

        var pid = session.Child.ProcessId;
        Assert.True(JobLimits.IsInJob(pid));
        // The limits Windows really holds for the job, not just what the gateway asked for.
        var (limits, killOnClose) = JobLimits.Read(session.Job);
        Assert.Equal(new ResourceLimits(1024L * 1024 * 1024, 5), limits);
        Assert.True(killOnClose);
        session.Job.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!session.Child.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        Assert.True(session.Child.HasExited);
    }
}
