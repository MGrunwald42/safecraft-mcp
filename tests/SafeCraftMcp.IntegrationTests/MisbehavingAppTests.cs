using System.Diagnostics;
using System.Text.Json;
using SafeCraftMcp.ChildRpc;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Mxc.Sdk;
using Microsoft.Mxc.Sdk.V1;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.IntegrationTests;

// ChildConnection against an app that misbehaves (tests/HostileWorkload's fake-MCP mode, which runs
// in exactly a session's sandbox): one that never answers the handshake, floods stdout or stderr
// without a newline, crashes mid-call or ignores the end of stdin. Whatever it does, the gateway
// must end the connection promptly, keep its memory bounded and leave no process behind.
[Collection(HostileWorkloadCollection.Name)]
[Trait("Category", "Mxc")]
public sealed class MisbehavingAppTests(HostileWorkloadBuild build) : IAsyncDisposable
{
    private const string App = "fakecraft";
    private const long MaxMessageBytes = 1024 * 1024;

    private readonly string _state = Directory.CreateTempSubdirectory("safecraft-misbehave-").FullName;
    private readonly List<SafeFileHandle> _jobs = [];
    private SessionManager? _sessions;

    public async ValueTask DisposeAsync()
    {
        // The jobs first: closing one kills whatever a test left running in its sandbox.
        foreach (var job in _jobs)
            job.Dispose();
        if (_sessions is not null)
            await _sessions.DisposeAsync();
        DeleteFolder(_state);
    }

    // A fresh sandbox running the fake in <behavior>, built like a session's (SessionManager.SpecFor).
    private async Task<(MxcProcess Process, SafeFileHandle Job)> Spawn(string behavior)
    {
        Assert.Null(build.Error);
        var host = MxcHost.TestHost(_state);
        var settings = GatewaySettings.Defaults(host) with { Repos = HostPaths.Current().DefaultRepos };
        _sessions ??= new SessionManager(settings, AppRegistry.LoadEmbedded(), new BinaryResolver(settings), host, NullLoggerFactory.Instance);
        var app = new AppManifest
        {
            Id = App, Display = "FakeCraft", Repo = "-", Binary = "fakecraft-cli.exe", Search = [], Args = ["mcp", behavior],
            Ready = true, CallTimeoutSeconds = 60, Import = [], Export = [], MaxFileMb = 1,
        };
        var frozen = BinaryFreezer.Freeze(build.Exe, app, host.StateDir, pin: null);
        var id = "s" + Guid.NewGuid().ToString("N")[..8];
        var request = SandboxRequest.Build(_sessions.SpecFor(app, frozen.Path, SessionPaths.Create(host.SessionsDir, id), id));
        var (process, _, job) = await SandboxLauncher.SpawnAsync(request, settings.MinTier, _sessions.Limits);
        _jobs.Add(job);
        return (process, job);
    }

    private static Task<ChildConnection> Connect(MxcProcess process, TimeSpan timeout, long maxMessageBytes = MaxMessageBytes) =>
        ChildConnection.ConnectAsync(process, App, timeout, maxMessageBytes);

    private static async Task<string> Echo(ChildConnection connection)
    {
        var result = await connection.CallToolAsync("echo", new Dictionary<string, JsonElement>(), null, CancellationToken.None);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
    }

    // The sandbox's whole process tree is gone, not just disowned. Asked of the job the test still
    // holds, not of a process id, which Windows may hand to another process once the app is gone.
    private static async Task AssertGone(SafeFileHandle job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (JobLimits.ActiveProcesses(job) > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(JobLimits.ActiveProcesses(job) == 0, $"{JobLimits.ActiveProcesses(job)} of the app's processes are still running.");
    }

    private static async Task AssertExited(ChildConnection connection)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!connection.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(connection.HasExited);
    }

    [MxcFact]
    public async Task A_Control_App_Connects_And_Answers()
    {
        var (process, _) = await Spawn("ok");
        await using var connection = await Connect(process, TimeSpan.FromSeconds(30));

        Assert.Equal(["echo", "command_list"], connection.Tools.Select(t => t.Name));
        Assert.Equal("pong", await Echo(connection));
        Assert.False(connection.HasExited);
    }

    [MxcFact]
    public async Task An_App_That_Never_Answers_The_Handshake_Is_Killed_At_The_Timeout()
    {
        var (process, job) = await Spawn("silent-handshake");
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<ChildException>(() => Connect(process, TimeSpan.FromSeconds(2)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
        Assert.Contains("didn't complete the MCP handshake", error.Message);
        Assert.Contains("didn't answer within 2 s", error.Message);
        await AssertGone(job);
    }

    [MxcFact]
    public async Task An_Endless_Stdout_Line_Is_Cut_Off_By_The_Line_Limit()
    {
        var (process, job) = await Spawn("flood-stdout");
        var watch = Stopwatch.StartNew();

        // A generous handshake timeout: the limit, not the clock, has to be what ends it.
        var error = await Assert.ThrowsAsync<ChildException>(() => Connect(process, TimeSpan.FromSeconds(30), maxMessageBytes: 64 * 1024));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Took {watch.Elapsed}.");
        Assert.Contains("longer than 65536 bytes", error.Message);
        await AssertGone(job);
    }

    [MxcFact]
    public async Task A_Stderr_Flood_Without_A_Newline_Keeps_Only_The_Last_16K_And_The_Connection_Works()
    {
        var (process, _) = await Spawn("flood-stderr");
        var before = GC.GetTotalMemory(forceFullCollection: true);
        await using var connection = await Connect(process, TimeSpan.FromSeconds(30));

        // Calls go through while the flood is still on.
        Assert.Equal("pong", await Echo(connection));
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!connection.StderrTail.EndsWith("<END>", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        var tail = connection.StderrTail;
        var growth = GC.GetTotalMemory(forceFullCollection: true) - before;

        // 32 MB went through; what's left is the last 16 K chars, ending with the marker.
        Assert.EndsWith("<END>", tail);
        Assert.Equal(16 * 1024, tail.Length);
        Assert.True(growth < 16 * 1024 * 1024, $"The gateway's managed memory grew by {growth / 1024} KB.");
        Assert.Equal("pong", await Echo(connection));
        Assert.False(connection.HasExited);
    }

    [MxcFact]
    public async Task An_App_That_Crashes_Mid_Call_Fails_The_Call_At_Once()
    {
        var (process, _) = await Spawn("crash-on-call");
        await using var connection = await Connect(process, TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Echo(connection));

        // The kinds of failure SessionTools turns into "the app failed, so the session was closed".
        Assert.True(error is McpException or IOException or ObjectDisposedException, error.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
        await AssertExited(connection);
        Assert.False(await connection.PingAsync(TimeSpan.FromSeconds(1)));
    }

    [MxcFact]
    public async Task An_App_That_Ignores_The_End_Of_Stdin_Is_Killed_After_The_Grace_Period()
    {
        var (process, job) = await Spawn("ignore-eof");
        // await using too, in case an assertion fails before the explicit dispose (a second one is a no-op).
        await using var connection = await Connect(process, TimeSpan.FromSeconds(30));
        Assert.Equal("pong", await Echo(connection));
        var watch = Stopwatch.StartNew();

        await connection.DisposeAsync();

        // The 2 s grace is spent (it really ignored stdin), but not much more.
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(8));
        Assert.True(connection.HasExited);
        await AssertGone(job);
    }
}
