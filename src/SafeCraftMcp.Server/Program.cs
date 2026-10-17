using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// safecraft-mcp: the MCP gateway. stdout carries only MCP JSON-RPC; everything else goes to stderr.
// Settings come only from this command line, parsed once before the server starts.

var parsed = SettingsParser.ParseCommandLine(args);
if (SettingsParser.IsInformational(parsed))
    return await parsed.InvokeAsync();

HostPaths host;
GatewaySettings settings;
var registry = AppRegistry.LoadEmbedded();
try
{
    host = HostPaths.Current();
    settings = SettingsParser.FromParseResult(parsed, registry.Ids, host, RealPath.ResolveFolder);
}
catch (Exception ex) when (ex is SettingsException or InvalidOperationException)
{
    Console.Error.WriteLine("safecraft-mcp: the gateway didn't start:");
    foreach (var error in ex is SettingsException refused ? refused.Errors : [ex.Message])
        Console.Error.WriteLine("  " + error);
    Console.Error.WriteLine("Run safecraft-mcp --help for the options.");
    return 2;
}

var protectedPaths = SandboxRequest.UsableDeniedPaths(settings.ProtectedPaths(host));
var binaries = new BinaryResolver(settings);
var info = new InfoTools(settings, registry, binaries, host, args,
    () => TierGate.Describe(TierGate.ProbeRequest(protectedPaths)));
// Set once the host (and its logging) exists; the MCP handlers only run after that.
ToolCatalog? catalog = null;
GuideTools? guides = null;

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
// A log file that can't be created (a locked or read-only folder) only costs the file, not the gateway.
FileLog? fileLog = null;
try
{
    fileLog = new FileLog(Path.Combine(host.StateDir, "logs"));
    builder.Logging.AddProvider(fileLog);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"safecraft-mcp: no log file this time ({ex.Message}); logging to stderr only.");
}
using var ownedFileLog = fileLog;
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Services
    .AddMcpServer(options =>
    {
        var version = typeof(ToolCatalog).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        options.ServerInfo = new Implementation { Name = "safecraft-mcp", Version = version };
        options.ServerInstructions = ServerText.Instructions;
        // A tools/call the SDK can't deserialize gets -32602 here instead of the SDK's -32603.
        options.Filters.Message.IncomingFilters.Add(next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.ToolsCall } request
                && ToolCatalog.MalformedCall(request.Params) is { } problem)
            {
                await context.Server.SendMessageAsync(new JsonRpcError
                {
                    Id = request.Id,
                    Error = new JsonRpcErrorDetail { Code = (int)McpErrorCode.InvalidParams, Message = problem },
                }, cancellationToken);
                return;
            }
            await next(context, cancellationToken);
        });
        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(catalog!.List());
        options.Handlers.CallToolHandler = async (request, cancellationToken) =>
        {
            var progress = request.Params!.ProgressToken is { } token ? new ClientProgress(request.Server, token) : null;
            return await catalog!.CallAsync(request.Params.Name, request.Params.Arguments, progress, cancellationToken);
        };
        options.Handlers.ListPromptsHandler = (_, _) => ValueTask.FromResult(guides!.ListPrompts());
        options.Handlers.GetPromptHandler = (request, _) =>
            ValueTask.FromResult(guides!.GetPrompt(request.Params!.Name, request.Params.Arguments?.AsReadOnly()));
        // The apps' own docs only with --upstream-docs; without it the gateway serves no resources.
        if (settings.UpstreamDocs)
        {
            options.Handlers.ListResourcesHandler = (_, _) => ValueTask.FromResult(guides!.ListDocs());
            options.Handlers.ReadResourceHandler = (request, _) => ValueTask.FromResult(guides!.ReadDoc(request.Params!.Uri));
        }
    })
    .WithStdioServerTransport();

var app = builder.Build();
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
await using var sessions = new SessionManager(settings, registry, binaries, host, loggerFactory);
var workspace = new WorkspaceTools(sessions, settings, host);
guides = new GuideTools(sessions, registry, binaries, settings);
catalog = new ToolCatalog(
[
    .. info.Tools(),
    .. guides.Tools(),
    .. new SessionTools(sessions, workspace, settings, loggerFactory.CreateLogger<SessionTools>()).Tools(),
    .. workspace.Tools(),
]);
// Stdin EOF and Ctrl-C end RunAsync, and the await using closes every session. On a hard exit,
// ProcessExit still kills the sandboxes; MXC's lifetime timeout is the last backstop.
AppDomain.CurrentDomain.ProcessExit += (_, _) => sessions.KillAll();

await app.RunAsync();
return 0;
