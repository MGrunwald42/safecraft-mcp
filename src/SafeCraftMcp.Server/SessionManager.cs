using System.Collections.Concurrent;
using System.Security.Cryptography;
using SafeCraftMcp.ChildRpc;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Mxc.Sdk.V1;
using Microsoft.Win32.SafeHandles;

namespace SafeCraftMcp.Server;

/// <summary>A running app in its sandbox.</summary>
public sealed class Session(string id, AppManifest app, SessionPaths paths, string workReal, FrozenBinary binary, IsolationTier tier, ChildConnection child, SafeFileHandle job)
{
    private int _activeCalls;

    public string Id { get; } = id;
    public AppManifest App { get; } = app;
    public SessionPaths Paths { get; } = paths;

    /// <summary>The workspace's real path (links and short names resolved), the root file transfers check against.</summary>
    public string WorkReal { get; } = workReal;
    public FrozenBinary Binary { get; } = binary;
    public IsolationTier Tier { get; } = tier;
    public ChildConnection Child { get; } = child;

    /// <summary>The Job Object with the session's resource limits; closing it kills the whole tree.</summary>
    public SafeFileHandle Job { get; } = job;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
    public bool Busy => Volatile.Read(ref _activeCalls) > 0;

    /// <summary>Imports run one at a time per session, so two can't both pass the workspace total cap.</summary>
    public SemaphoreSlim ImportLock { get; } = new(1, 1);

    /// <summary>Marks a call as running until the returned scope is disposed, so the idle sweep leaves the session alone.</summary>
    public IDisposable Use()
    {
        Interlocked.Increment(ref _activeCalls);
        LastUsedUtc = DateTime.UtcNow;
        return new Scope(this);
    }

    private sealed class Scope(Session session) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1)
                return;
            session.LastUsedUtc = DateTime.UtcNow;
            Interlocked.Decrement(ref session._activeCalls);
        }
    }
}

/// <summary>A request the gateway refuses; the message goes to the agent as a tool error.</summary>
public sealed class GatewayRefusalException(string message) : Exception(message);

/// <summary>
/// Opens, tracks and closes sessions. Opening checks the app against the
/// settings, freezes its binary (and checks the pin), creates the session folders, builds the
/// sandbox request and goes through <see cref="SandboxLauncher"/>. A sweep closes sessions that
/// were idle too long, lived too long or whose app died. Closing deletes the session folders
/// unless <c>--keep-workspace</c>; disposing the manager closes every session.
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    // How long the app gets to answer the MCP handshake after its sandbox started.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    public ResourceLimits Limits => new((long)_settings.MaxMemoryMb * 1024 * 1024, _settings.MaxProcesses);

    private long MaxMessageBytes =>
        // One JSON-RPC line from the app: the response cap plus base64's third and the JSON around
        // it. A longer line can't become an allowed response, so the app is killed instead.
        (long)_settings.MaxResponseMb * 1024 * 1024 * 3 / 2 + 1024 * 1024;

    private readonly GatewaySettings _settings;
    private readonly AppRegistry _registry;
    private readonly BinaryResolver _binaries;
    private readonly HostPaths _host;
    private readonly IReadOnlyList<string> _protectedPaths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Lock _slots = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sweeper;
    private int _opening;
    private bool _disposed;

    public SessionManager(GatewaySettings settings, AppRegistry registry, BinaryResolver binaries, HostPaths host, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _registry = registry;
        _binaries = binaries;
        _host = host;
        _protectedPaths = SandboxRequest.UsableDeniedPaths(settings.ProtectedPaths(host));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SessionManager>();
        // Kept workspaces carry a marker, so this leaves them alone whatever this gateway's options.
        foreach (var orphan in SessionPaths.DeleteOrphans(host.SessionsDir).Concat(SessionPaths.DeleteOrphans(host.LegacySessionsDir)))
            _logger.LogInformation("Deleted {Folder}, left by a gateway that's gone.", orphan);
        BinaryFreezer.DeleteStaleTemps(host.StateDir);
        foreach (var unused in BinaryFreezer.DeleteUnused(host.StateDir, TimeSpan.FromDays(14)))
            _logger.LogInformation("Deleted {Folder}, a frozen binary no session used for two weeks.", unused);
        _sweeper = SweepLoopAsync(_stop.Token);
    }

    public IReadOnlyCollection<Session> Sessions => [.. _sessions.Values];

    public Session Get(string id) =>
        _sessions.TryGetValue(id, out var session)
            ? session
            : throw new GatewayRefusalException($"No open session '{id}'. It may have been closed after being idle, or its app exited; open a new one.");

    public async Task<Session> OpenAsync(string appId, CancellationToken cancellationToken)
    {
        var app = _registry.Find(appId) ?? throw new GatewayRefusalException($"Unknown app '{appId}'. Known: {string.Join(", ", _registry.Ids)}.");
        if (_settings.Apps is not null && !_settings.Apps.Contains(app.Id))
            throw new GatewayRefusalException($"{app.Display} isn't in --apps ({string.Join(", ", _settings.Apps)}); the user can add it in the MCP server config.");
        if (!app.Ready)
            throw new GatewayRefusalException($"{app.Display} hasn't had its sandbox check yet, so it can't be opened.");
        var found = _binaries.Find(app) ?? throw new GatewayRefusalException($"No {app.Binary} found. Build it with update-craft.");
        if (found.Sha256 is null)
            throw new GatewayRefusalException(found.Error ?? "The binary couldn't be read.");

        lock (_slots)
        {
            if (_disposed)
                throw new GatewayRefusalException("The gateway is stopping.");
            if (_sessions.Count + _opening >= _settings.MaxSessions)
                throw new GatewayRefusalException($"{_settings.MaxSessions} sessions are open (--max-sessions); close one with session_close first.");
            _opening++;
        }
        SessionPaths? paths = null;
        try
        {
            _settings.Pins.TryGetValue(app.Id, out var pin);
            FrozenBinary binary;
            try
            {
                binary = BinaryFreezer.Freeze(found.Path, app, _host.StateDir, pin);
            }
            catch (InvalidOperationException ex)
            {
                throw new GatewayRefusalException(ex.Message);
            }
            var id = "s" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            try
            {
                paths = SessionPaths.Create(_host.SessionsDir, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GatewayRefusalException($"Couldn't create the session folder under {_host.SessionsDir}: {ex.Message}");
            }
            var workReal = RealPath.ResolveFolder(paths.Work, out var workError)
                ?? throw new GatewayRefusalException($"The session workspace {paths.Work} isn't usable: {workError}");
            var request = SandboxRequest.Build(SpecFor(app, binary.Path, paths, id));
            var (process, tier, job) = await SandboxLauncher.SpawnAsync(request, _settings.MinTier, Limits, cancellationToken);
            ChildConnection child;
            try
            {
                child = await ChildConnection.ConnectAsync(process, app.Id, HandshakeTimeout, MaxMessageBytes, _loggerFactory, cancellationToken);
            }
            catch (ChildException ex)
            {
                job.Dispose();
                throw new GatewayRefusalException(Untrusted.WithAppText(ex.Message, app.Display, null, ex.AppOutput));
            }
            catch
            {
                job.Dispose();
                throw;
            }
            var session = new Session(id, app, paths, workReal, binary, tier, child, job);
            bool stopping;
            lock (_slots)
            {
                stopping = _disposed;
                if (!stopping)
                    _sessions[id] = session;
            }
            if (stopping)
            {
                // DisposeAsync ran while this one started; nothing else would close it.
                await child.DisposeAsync();
                job.Dispose();
                throw new GatewayRefusalException("The gateway is stopping.");
            }
            _logger.LogInformation("Opened {Session}: {App} {Sha} at {Tier} in {Folder}.", id, app.Id, binary.Sha256, tier, paths.Root);
            return session;
        }
        catch (SandboxRefusedException ex)
        {
            DeleteQuietly(paths);
            throw new GatewayRefusalException(ex.Message);
        }
        catch
        {
            DeleteQuietly(paths);
            throw;
        }
        finally
        {
            lock (_slots)
                _opening--;
        }
    }

    public AppManifest? FindApp(string id) => _registry.Find(id);

    /// <summary>The sandbox a session of <paramref name="app"/> runs in; the escape suite runs its workload in the same one.</summary>
    public SandboxSpec SpecFor(AppManifest app, string executable, SessionPaths paths, string id) => new()
    {
        Executable = executable,
        Arguments = app.Args,
        Environment = app.Env,
        Paths = paths,
        ContainerName = $"safecraft-mcp-{Environment.ProcessId}-{id}",
        DeniedPaths = _protectedPaths,
        MountedGrants = _settings.MountGrants ? _settings.Grants : [],
        Lifetime = _settings.MaxLifetime,
    };

    /// <param name="discard">Delete the workspace even with <c>--keep-workspace</c> (the gateway's own throwaway sessions).</param>
    public async Task<bool> CloseAsync(string id, string reason, bool discard = false)
    {
        if (!_sessions.TryRemove(id, out var session))
            return false;
        try
        {
            await session.Child.DisposeAsync();
        }
        finally
        {
            // Closing the job kills whatever is left of the tree, whatever the dispose did.
            session.Job.Dispose();
        }
        if (_settings.KeepWorkspace && !discard)
            session.Paths.MarkKept();
        else
            DeleteQuietly(session.Paths);
        _logger.LogInformation("Closed {Session} ({Reason}).", id, reason);
        return true;
    }

    /// <summary>Closes sessions that idled too long, lived too long or whose app exited.</summary>
    public async Task SweepAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var session in Sessions)
        {
            var reason = session.Child.HasExited ? "the app exited"
                : now - session.StartedUtc > _settings.MaxLifetime ? "it reached --max-lifetime"
                : !session.Busy && now - session.LastUsedUtc > _settings.IdleTimeout ? "it was idle longer than --idle-timeout"
                : null;
            if (reason is null)
                continue;
            try
            {
                await CloseAsync(session.Id, reason);
            }
            catch (Exception ex)
            {
                // A background loop: one session failing to close must not stop the sweep.
                _logger.LogError(ex, "Closing {Session} ({Reason}) failed.", session.Id, reason);
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await SweepAsync();
        }
        catch (OperationCanceledException)
        {
            // The gateway is stopping.
        }
    }

    /// <summary>
    /// For process exit, where async work can't finish: starts every session's dispose (stdin
    /// closed, then a kill) and waits at most a few seconds in all. A sandbox still running when the
    /// gateway's process ends dies with it.
    /// </summary>
    public void KillAll()
    {
        var closing = Sessions.Select(s => s.Child.DisposeAsync().AsTask()).ToArray();
        Task.WaitAll(closing, TimeSpan.FromSeconds(3));
        // Closing the jobs kills whatever is still running in them.
        foreach (var session in Sessions)
            session.Job.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_slots)
            _disposed = true;
        await _stop.CancelAsync();
        await _sweeper;
        foreach (var id in _sessions.Keys)
        {
            try
            {
                await CloseAsync(id, "the gateway is stopping");
            }
            catch (Exception ex)
            {
                // Shutdown: close the others whatever went wrong with this one.
                _logger.LogError(ex, "Closing {Session} at shutdown failed.", id);
            }
        }
        _stop.Dispose();
    }

    private void DeleteQuietly(SessionPaths? paths)
    {
        try
        {
            paths?.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by an exiting sandbox; the next start deletes orphans.
            _logger.LogWarning("Couldn't delete {Folder}: {Message}", paths!.Root, ex.Message);
        }
    }
}
