using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Mxc.Sdk;
using Microsoft.Mxc.Sdk.V1;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.ChildRpc;

/// <summary>
/// The gateway's MCP client for one sandboxed app: the MCP SDK's client over the
/// <see cref="MxcProcess"/> stdin and stdout (M0/M1 showed it works there). It performs the
/// handshake, keeps the app's tool list, and forwards calls with their progress and cancellation.
/// Everything it returns came from an untrusted program; labeling it is the caller's job.
/// Untrusted input is bounded where it enters: one stdout line (message) at most
/// <c>maxMessageBytes</c>, and only the last part of stderr is kept, read in fixed chunks.
/// A task waits on the process the whole time: it notices an exit, and it's what makes MXC
/// enforce the request's <c>TimeoutMs</c> (MXC checks it only while something waits).
/// Disposing closes stdin, gives the app a moment to exit, then kills the whole sandbox tree.
/// </summary>
public sealed class ChildConnection : IAsyncDisposable
{
    // How long a closed app may take to exit by itself before it's killed.
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);
    // Enough of the app's stderr to explain a crash.
    private const int StderrTailChars = 16 * 1024;
    // How long telling the app about a cancelled call may take (a full stdin pipe would block it).
    private static readonly TimeSpan CancelSendTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The MCP revision the gateway speaks to every app.</summary>
    public const string ChildProtocolVersion = "2025-06-18";

    private readonly MxcProcess _process;
    private readonly McpClient _client;
    private readonly Task<WaitResult> _exit;
    private readonly StderrRing _stderr;
    private int _disposed;
    private long _lastCallId;

    private ChildConnection(MxcProcess process, McpClient client, Task<WaitResult> exit, StderrRing stderr, IReadOnlyList<Tool> tools)
    {
        _process = process;
        _client = client;
        _exit = exit;
        _stderr = stderr;
        Tools = tools;
    }

    public IReadOnlyList<Tool> Tools { get; }

    /// <summary>True once the app process has exited (crashed, killed, timed out or closed), or the connection is disposed.</summary>
    public bool HasExited => _exit.IsCompleted || Volatile.Read(ref _disposed) == 1;

    /// <summary>The sandboxed process's id (for the Job Object and diagnostics).</summary>
    public uint ProcessId => _process.Id;

    /// <summary>The last part of the app's stderr (untrusted text).</summary>
    public string StderrTail => _stderr.Text;

    /// <summary>Connects to a freshly spawned app. On any failure the process is killed and disposed.</summary>
    /// <param name="maxMessageBytes">The longest stdout line (one JSON-RPC message) accepted before the app is killed.</param>
    public static async Task<ChildConnection> ConnectAsync(
        MxcProcess process, string name, TimeSpan timeout, long maxMessageBytes,
        ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger($"child.{name}");
        if (process.StandardInput is not { } stdin || process.StandardOutput is not { } stdout || process.StandardError is not { } stderrStream)
        {
            KillAndDispose(process);
            throw new ChildException($"{name}'s sandbox didn't provide piped stdin, stdout and stderr.");
        }
        var stderr = new StderrRing(stderrStream, StderrTailChars, logger);
        var exit = process.WaitAsync();
        var output = new LineLimitStream(stdout, maxMessageBytes, () => KillQuietly(process));
        McpClient? client = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        // A hung app is killed the moment the handshake times out, not after the SDK's own cleanup.
        using var killOnTimeout = limit.Token.Register(() => KillQuietly(process));
        try
        {
            var transport = new StreamClientTransport(stdin, output, loggerFactory);
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "safecraft-mcp", Version = "1" },
                // Pinned: left open, the SDK sometimes (timing-dependent, under load) takes the
                // 2026-07-28 path with per-request _meta, which PhotoCraft 0.8.2 then refuses.
                // 2025-06-18 is the classic initialize handshake every app in the suite speaks.
                ProtocolVersion = ChildProtocolVersion,
                // `limit` is the only clock: a second timer of the SDK's could win the race and
                // fail with its own message instead of "didn't answer within".
                InitializationTimeout = Timeout.InfiniteTimeSpan,
            };
            client = await McpClient.CreateAsync(transport, options, loggerFactory, limit.Token);
            var tools = (await client.ListToolsAsync(cancellationToken: limit.Token)).Select(t => t.ProtocolTool).ToList();
            return new ChildConnection(process, client, exit, stderr, tools);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            KillQuietly(process);
            if (client is not null)
                await DisposeQuietly(client);
            await stderr.Done.WaitAsync(ExitGrace).ContinueWith(_ => { }, TaskScheduler.Default);
            KillAndDispose(process);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            // An McpException's message quotes the app's own error text, so it goes with the app output.
            var reason = output.Overflowed ? $"it wrote a message longer than {maxMessageBytes} bytes"
                : limit.IsCancellationRequested ? $"it didn't answer within {timeout.TotalSeconds:0} s"
                : ex is McpException ? "it answered with an error"
                : ex.Message;
            var appOutput = (ex is McpException ? ex.Message + "\n" : "") + stderr.Text;
            throw new ChildException($"{name} didn't complete the MCP handshake: {reason}.", appOutput, ex);
        }
    }

    /// <summary>
    /// Calls an app tool under a request id of the gateway's own, so a cancelled call (by the
    /// client or a timeout) is cancelled in the app too: the SDK's client (2.2.0) sends no
    /// <c>notifications/cancelled</c> itself. The id doubles as the progress token.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(
        string tool, IReadOnlyDictionary<string, JsonElement> arguments, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var id = $"gw-{Interlocked.Increment(ref _lastCallId)}";
        var call = new CallToolRequestParams { Name = tool, Arguments = new Dictionary<string, JsonElement>(arguments) };
        IAsyncDisposable? progressHandler = null;
        if (progress is not null)
        {
            call.Meta = new JsonObject { ["progressToken"] = id };
            progressHandler = _client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (notification, _) =>
            {
                if (ProgressOf(notification, id) is { } value)
                    progress.Report(value);
                return ValueTask.CompletedTask;
            });
        }
        try
        {
            return await _client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                RequestMethods.ToolsCall, call, McpJsonUtilities.DefaultOptions, new RequestId(id), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !HasExited)
        {
            await CancelInAppAsync(id);
            throw;
        }
        finally
        {
            if (progressHandler is not null)
                await progressHandler.DisposeAsync();
        }
    }

    // Sent before the cancelled call returns, so it reaches the app ahead of the caller's next request.
    private async Task CancelInAppAsync(string id)
    {
        using var limit = new CancellationTokenSource(CancelSendTimeout);
        try
        {
            await _client.SendNotificationAsync(NotificationMethods.CancelledNotification,
                new CancelledNotificationParams { RequestId = new RequestId(id) }, McpJsonUtilities.DefaultOptions, limit.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or McpException)
        {
            // The app is gone or not reading; the caller's ping or close deals with that.
        }
    }

    private static readonly JsonTypeInfo<ProgressNotificationParams> ProgressParamsInfo =
        (JsonTypeInfo<ProgressNotificationParams>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ProgressNotificationParams));

    // The app's notification is untrusted: one that doesn't parse, or is for another call, is dropped.
    private static ProgressNotificationValue? ProgressOf(JsonRpcNotification notification, string id)
    {
        try
        {
            var parameters = notification.Params?.Deserialize(ProgressParamsInfo);
            return parameters is not null && parameters.ProgressToken.Equals(new ProgressToken(id)) ? parameters.Progress : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken) =>
        _client.ReadResourceAsync(uri, cancellationToken: cancellationToken).AsTask();

    /// <summary>
    /// Whether the app still answers, within <paramref name="timeout"/>. Any answer counts, an error
    /// too: PhotoCraft 0.8.2 answers <c>ping</c> with an error response but keeps serving.
    /// </summary>
    public async Task<bool> PingAsync(TimeSpan timeout)
    {
        if (HasExited)
            return false;
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await _client.PingAsync(cancellationToken: limit.Token);
            return true;
        }
        catch (McpProtocolException)
        {
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or McpException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            // stdin first: the app exits on EOF, its stdout ends, and the SDK client's dispose
            // returns at once. An app that ignores EOF is killed after the grace period, before
            // the client is disposed (which would otherwise wait for stdout to end).
            try
            {
                _process.StandardInput?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already closed.
            }
            await _exit.WaitAsync(ExitGrace).ContinueWith(_ => { }, TaskScheduler.Default);
            KillQuietly(_process);
            await DisposeQuietly(_client);
            await _stderr.Done.WaitAsync(ExitGrace).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            KillAndDispose(_process);
        }
    }

    private static async Task DisposeQuietly(McpClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or McpException or OperationCanceledException)
        {
            // The app is gone or going; nothing left to close cleanly.
        }
    }

    private static void KillQuietly(MxcProcess process)
    {
        try
        {
            if (!process.TryGetExitCode(out _))
                process.Kill();
        }
        catch (Exception ex) when (ex is MxcException or ObjectDisposedException)
        {
            // Already exited or already disposed.
        }
    }

    private static void KillAndDispose(MxcProcess process)
    {
        KillQuietly(process);
        try
        {
            process.Dispose();
        }
        catch (Exception ex) when (ex is MxcException or ObjectDisposedException)
        {
            // Disposed twice is fine.
        }
    }

    /// <summary>Drains the app's stderr in fixed chunks and keeps only the last part.</summary>
    private sealed class StderrRing
    {
        private readonly char[] _ring;
        private readonly Lock _lock = new();
        private int _length;
        private int _start;

        public StderrRing(Stream stream, int capacity, ILogger logger)
        {
            _ring = new char[capacity];
            Done = Task.Run(async () =>
            {
                using var reader = new StreamReader(stream);
                var buffer = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    Append(buffer.AsSpan(0, read));
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("{Chunk}", new string(buffer, 0, read));
                }
            });
        }

        public Task Done { get; }

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    var chars = new char[_length];
                    for (var i = 0; i < _length; i++)
                        chars[i] = _ring[(_start + i) % _ring.Length];
                    return new string(chars);
                }
            }
        }

        private void Append(ReadOnlySpan<char> chunk)
        {
            lock (_lock)
            {
                foreach (var c in chunk)
                {
                    _ring[(_start + _length) % _ring.Length] = c;
                    if (_length < _ring.Length)
                        _length++;
                    else
                        _start = (_start + 1) % _ring.Length;
                }
            }
        }
    }
}

/// <summary>
/// The app failed in a way that ends the session (handshake, crash). <see cref="Exception.Message"/>
/// is the gateway's own description; <see cref="AppOutput"/> is what the app wrote (untrusted), kept
/// apart so the caller labels it before it reaches the agent.
/// </summary>
public sealed class ChildException(string message, string appOutput = "", Exception? inner = null) : Exception(message, inner)
{
    public string AppOutput { get; } = appOutput;
}
