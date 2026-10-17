using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.Server;

/// <summary>
/// The session tools: open a sandboxed app, list and call its own tools, read its
/// resources, close it. Everything that comes out of the sandbox is labeled as untrusted app output
/// (local rule L6), error messages and tool names included. The gateway caps previews and response
/// sizes, applies the call and export timeouts, and forwards progress and cancellation.
/// <c>session_open</c> takes only the app: no tier, path, network, flag or environment.
/// </summary>
public sealed class SessionTools(SessionManager sessions, WorkspaceTools workspace, GatewaySettings settings, ILogger<SessionTools> logger)
{
    // After a timed-out call is cancelled, how long the app gets to show it still answers.
    private static readonly TimeSpan RecoveryPing = TimeSpan.FromSeconds(5);

    private static JsonObject StringArg(string description) => new() { ["type"] = "string", ["description"] = description };

    public IEnumerable<GatewayTool> Tools() =>
    [
        new()
        {
            Name = "session_open",
            Title = "Open app session",
            Description = "Starts an app's headless MCP server in a new sandbox (no network, no UI, only its own empty " +
                "workspace) and returns the session id and the app's tool names. With project, a file from a read grant is " +
                "imported into the workspace and opened. Close the session with session_close when done; idle sessions close " +
                "by themselves. Call crafts_list first to see which apps can be opened.",
            Arguments = new Dictionary<string, JsonObject>
            {
                ["app"] = StringArg("App id from crafts_list, e.g. photocraft."),
                ["project"] = StringArg("Optional: a file to open, as <read grant>/<path in it>, e.g. in/photos/cat.psd."),
            },
            Required = ["app"],
            ReadOnly = false, Destructive = false, Idempotent = false,
            Run = OpenAsync,
        },
        new()
        {
            Name = "session_tools",
            Title = "List app tools",
            Description = "The app's own tools with their input schemas and hints, as the sandboxed app reports them " +
                "(untrusted). Call them with session_call.",
            Arguments = new Dictionary<string, JsonObject> { ["session"] = StringArg("Session id from session_open.") },
            Required = ["session"],
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = ToolsAsync,
        },
        new()
        {
            Name = "session_call",
            Title = "Call app tool",
            Description = "Calls one of the app's tools inside its sandbox and returns its result (text, images) labeled as " +
                "untrusted app output. Files the app writes stay in its workspace. Previews are capped at --max-preview-side.",
            Arguments = new Dictionary<string, JsonObject>
            {
                ["session"] = StringArg("Session id from session_open."),
                ["tool"] = StringArg("The app's tool name, from session_tools (e.g. command_run, render_preview)."),
                ["arguments"] = new() { ["type"] = "object", ["description"] = "The app tool's arguments, as its input schema says." },
            },
            Required = ["session", "tool"],
            ReadOnly = false, Destructive = true, Idempotent = false,
            Run = CallAsync,
        },
        new()
        {
            Name = "session_read",
            Title = "Read app resource",
            Description = "Reads one of the app's resources, e.g. photocraft://document or photocraft://commands, labeled as untrusted app output.",
            Arguments = new Dictionary<string, JsonObject>
            {
                ["session"] = StringArg("Session id from session_open."),
                ["uri"] = StringArg("Resource URI with the app's scheme, e.g. photocraft://document."),
            },
            Required = ["session", "uri"],
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = ReadAsync,
        },
        new()
        {
            Name = "session_close",
            Title = "Close app session",
            Description = "Stops the app's sandbox and deletes its workspace (unless the gateway runs with --keep-workspace).",
            Arguments = new Dictionary<string, JsonObject> { ["session"] = StringArg("Session id from session_open.") },
            Required = ["session"],
            ReadOnly = false, Destructive = true, Idempotent = false,
            Run = CloseAsync,
        },
    ];

    private async Task<CallToolResult> OpenAsync(ToolCall call)
    {
        var appId = call.String("app").ToLowerInvariant();
        var project = call.OptionalString("project");
        var (grant, path) = project is null ? (null, null) : SplitProject(project);
        if (grant is not null && path is not null && sessions.FindApp(appId) is { } app)
        {
            // The cheap checks first, so a bad project doesn't start a sandbox.
            if (app.OpenTool is null || app.OpenToolPathArgument is null)
                throw new GatewayRefusalException($"{app.Display} has no tool to open a project with; open the session without one.");
            workspace.CheckImport(app, grant, path);
        }
        var session = await sessions.OpenAsync(appId, call.CancellationToken);
        CallToolResult? opened = null;
        if (grant is not null && path is not null)
        {
            try
            {
                opened = await OpenProjectAsync(session, grant, path, call);
            }
            catch (GatewayRefusalException ex)
            {
                // A session whose project didn't open is closed: the agent asked for both.
                await sessions.CloseAsync(session.Id, "its project couldn't be opened");
                throw new GatewayRefusalException($"The project couldn't be opened, so the session was closed. {ex.Message}");
            }
            catch
            {
                await sessions.CloseAsync(session.Id, "its project couldn't be opened");
                throw;
            }
        }
        var info = new JsonObject
        {
            ["session"] = session.Id,
            ["app"] = session.App.Id,
            ["tier"] = session.Tier.ToString(),
            ["binarySha256"] = session.Binary.Sha256,
            ["workspace"] = session.Paths.Work,
            ["project"] = project,
            ["next"] = "session_tools for the schemas, session_call to call one, session_close when done.",
        };
        // The tool names come from the app, so they follow its label.
        var names = Json.Array(session.Child.Tools.Select(t => t.Name));
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = Json.Indented(info) },
                new TextContentBlock { Text = Untrusted.Label(session.App.Display, session.Id) },
                new TextContentBlock { Text = "Tools: " + names.ToJsonString() },
                .. opened?.Content.Skip(1) ?? [],
            ],
            IsError = false,
        };
    }

    private static (string Grant, string Path) SplitProject(string project)
    {
        var at = project.IndexOfAny(['/', '\\']);
        if (at <= 0 || at == project.Length - 1)
            throw new GatewayRefusalException($"project '{project}' must be <read grant>/<path in it>, e.g. in/photos/cat.psd.");
        return (project[..at], project[(at + 1)..]);
    }

    private async Task<CallToolResult> OpenProjectAsync(Session session, string grant, string path, ToolCall call)
    {
        if (session.App is not { OpenTool: { } tool, OpenToolPathArgument: { } argument })
            throw new GatewayRefusalException($"{session.App.Display} has no tool to open a project with; open the session without one.");
        await workspace.ImportAsync(session, grant, path, name: null, call.CancellationToken);
        var arguments = new Dictionary<string, JsonElement> { [argument] = JsonSerializer.SerializeToElement(Path.GetFileName(path.Replace('\\', '/')), GatewayJsonContext.Default.String) };
        var result = await RunAsync(session, tool, settings.CallTimeoutFor(session.App.CallTimeoutSeconds), call,
            token => session.Child.CallToolAsync(tool, arguments, call.Progress, token));
        if (result.IsError is true)
            throw new GatewayRefusalException(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        return result;
    }

    private Task<CallToolResult> ToolsAsync(ToolCall call)
    {
        var session = sessions.Get(call.String("session"));
        using var _ = session.Use();
        var listed = new ListToolsResult { Tools = [.. session.Child.Tools] };
        var tools = JsonSerializer.SerializeToNode(listed, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsResult)))!["tools"]!;
        var result = new CallToolResult { Content = [new TextContentBlock { Text = Json.Indented(tools) }] };
        return Task.FromResult(Untrusted.Capped(session.App.Display, Untrusted.Labeled(session.App.Display, session.Id, result), settings.MaxResponseMb));
    }

    private Task<CallToolResult> CallAsync(ToolCall call)
    {
        var session = sessions.Get(call.String("session"));
        var tool = call.String("tool");
        if (session.Child.Tools.All(t => t.Name != tool))
            throw new GatewayRefusalException($"{session.App.Display} has no tool '{tool}'. session_tools lists them.");
        var arguments = new Dictionary<string, JsonElement>();
        if (call.Arguments.TryGetValue("arguments", out var given))
            foreach (var property in given.EnumerateObject())
                if (!arguments.TryAdd(property.Name, property.Value.Clone()))
                    throw new McpProtocolException($"session_call: 'arguments' has the key '{property.Name}' twice.", McpErrorCode.InvalidParams);
        if (session.App.PreviewTools.Contains(tool))
        {
            var side = Untrusted.PreviewSide(arguments.TryGetValue("max_side", out var asked) ? asked : null, settings.MaxPreviewSide);
            arguments["max_side"] = JsonSerializer.SerializeToElement(side, GatewayJsonContext.Default.Int32);
        }
        var limit = session.App.LongTools.Contains(tool) ? settings.ExportTimeout : settings.CallTimeoutFor(session.App.CallTimeoutSeconds);
        return RunAsync(session, tool, limit, call, token => session.Child.CallToolAsync(tool, arguments, call.Progress, token));
    }

    private async Task<CallToolResult> ReadAsync(ToolCall call)
    {
        var session = sessions.Get(call.String("session"));
        var uri = call.String("uri");
        if (!uri.StartsWith($"{session.App.Id}://", StringComparison.Ordinal))
            throw new GatewayRefusalException($"Only {session.App.Id}:// resources can be read from this session.");
        return await RunAsync(session, $"reading {uri}", settings.CallTimeoutFor(session.App.CallTimeoutSeconds), call, async token =>
        {
            var read = await session.Child.ReadResourceAsync(uri, token);
            return new CallToolResult { Content = [.. read.Contents.Select(c => (ContentBlock)new EmbeddedResourceBlock { Resource = c })] };
        });
    }

    /// <summary>
    /// Runs one request to the app with a timeout, and turns every way it can end into something
    /// the agent can act on, with the app's text labeled: a result, a refusal (the app's -32602),
    /// a timeout (the session closes if the app then doesn't answer a ping), the session being
    /// closed while it ran, or the app dying (the session closes).
    /// </summary>
    private async Task<CallToolResult> RunAsync(Session session, string what, TimeSpan limit, ToolCall call, Func<CancellationToken, Task<CallToolResult>> run)
    {
        var app = session.App.Display;
        using var timeout = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(call.CancellationToken, timeout.Token);
        using var _ = session.Use();
        try
        {
            var result = await run(linked.Token);
            return Untrusted.Capped(app, Untrusted.Labeled(app, session.Id, result), settings.MaxResponseMb);
        }
        catch (OperationCanceledException) when (call.CancellationToken.IsCancellationRequested)
        {
            // The client cancelled; ChildConnection already told the app.
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // ChildConnection sent notifications/cancelled to the app. If it doesn't answer a ping
            // now, it's stuck: the session is closed rather than left half alive.
            var recovered = await session.Child.PingAsync(RecoveryPing);
            if (!recovered)
                await sessions.CloseAsync(session.Id, $"{what} timed out and the app stopped answering");
            return ToolCatalog.Failure($"{what} didn't finish within {limit.TotalSeconds:0} s and was cancelled. " +
                (recovered ? "The session is still open; earlier changes may be partly applied, check with doc_inspect."
                    : "The app stopped answering, so the session was closed; open a new one."));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException && session.Child.HasExited)
        {
            return ToolCatalog.Failure($"The session {session.Id} was closed (or its app exited) while {what} ran; open a new one.");
        }
        catch (McpProtocolException ex)
        {
            // The app refused the request (bad arguments, unknown command). Its message is app output.
            throw new McpProtocolException(Untrusted.WithAppText($"{app} refused {what}:", app, session.Id, ex.Message), ex.ErrorCode);
        }
        catch (Exception ex) when (ex is McpException or IOException or ObjectDisposedException)
        {
            // The message may quote the app; the log gets only the gateway's own facts.
            logger.LogWarning("{What} in {Session} failed ({Error}); closing the session.", what, session.Id, ex.GetType().Name);
            var tail = session.Child.StderrTail;
            await sessions.CloseAsync(session.Id, $"the app failed during {what}");
            return ToolCatalog.Failure(Untrusted.WithAppText(
                $"The app failed during {what}, so the session was closed. Its error and the end of its stderr:", app, session.Id, ex.Message + "\n" + tail));
        }
    }

    private async Task<CallToolResult> CloseAsync(ToolCall call)
    {
        var id = call.String("session");
        if (!await sessions.CloseAsync(id, "session_close"))
            throw new GatewayRefusalException($"No open session '{id}'.");
        return ToolCatalog.Text(new JsonObject
        {
            ["closed"] = id,
            ["workspace"] = settings.KeepWorkspace ? "kept (--keep-workspace)" : "deleted",
        });
    }
}
