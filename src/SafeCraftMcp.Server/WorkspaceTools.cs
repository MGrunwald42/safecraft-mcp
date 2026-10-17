using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Settings;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.Server;

/// <summary>
/// Moving files between the user's grant folders and a session workspace. By
/// default there are no grants, and the app's files stay in its workspace. The copying is done by
/// the trusted gateway with every check in <see cref="FileTransfer"/>; this class only resolves
/// sessions and grants and applies the app's rules for the direction. File names listed from a
/// workspace were made by the app, so they're labeled as untrusted. The workspace total cap limits
/// what imports may add (one import at a time per session); it isn't a disk quota on what the app
/// itself writes. Copies get <c>--export-timeout</c> and the client's cancellation.
/// </summary>
public sealed class WorkspaceTools(SessionManager sessions, GatewaySettings settings, HostPaths host)
{
    // A workspace may hold up to twice the app's per-file cap in all.
    private const int WorkspaceCapFactor = 2;
    // workspace_list stops after this many entries; a hostile app could create millions.
    private const int MaxListed = 1000;

    private readonly IReadOnlyList<string> _protectedPaths = settings.ProtectedPaths(host);

    private static JsonObject StringArg(string description) => new() { ["type"] = "string", ["description"] = description };

    public IEnumerable<GatewayTool> Tools() =>
    [
        new()
        {
            Name = "grants_list",
            Title = "List grants",
            Description = "The folders the user granted for moving files in and out of sessions (--grant-read, --grant-write), " +
                "each with its access. None by default: then files stay in the session workspace.",
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = _ => Task.FromResult(ToolCatalog.Text(new JsonArray([.. settings.Grants.Select(g => (JsonNode?)new JsonObject
            {
                ["name"] = g.Name,
                ["access"] = g.Access.ToString().ToLowerInvariant(),
                ["path"] = g.Path,
            })]))),
        },
        new()
        {
            Name = "workspace_list",
            Title = "List workspace files",
            Description = "The files in a session's workspace (made by the app, so labeled as untrusted), with sizes. " +
                "Links aren't followed.",
            Arguments = new Dictionary<string, JsonObject> { ["session"] = StringArg("Session id from session_open.") },
            Required = ["session"],
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = call => Task.FromResult(List(sessions.Get(call.String("session")))),
        },
        new()
        {
            Name = "workspace_import",
            Title = "Import file into session",
            Description = "Copies one file from a read grant into the session's workspace, where the app can open it. Only the " +
                "app's import types, within its size cap; existing files are never overwritten.",
            Arguments = new Dictionary<string, JsonObject>
            {
                ["session"] = StringArg("Session id from session_open."),
                ["grant"] = StringArg("A read grant's name, from grants_list."),
                ["path"] = StringArg("The file's path inside the grant folder, e.g. photos/cat.jpg."),
                ["name"] = StringArg("File name in the workspace (default: the source's name)."),
            },
            Required = ["session", "grant", "path"],
            ReadOnly = false, Destructive = false, Idempotent = false,
            Run = call => ImportAsync(sessions.Get(call.String("session")), call.String("grant"), call.String("path"), call.OptionalString("name"), call.CancellationToken),
        },
        new()
        {
            Name = "workspace_export",
            Title = "Export file from session",
            Description = "Copies one file from the session's workspace into a write grant folder. Only the app's export types, " +
                "within its size cap; existing files are never overwritten. The file was made by an untrusted program.",
            Arguments = new Dictionary<string, JsonObject>
            {
                ["session"] = StringArg("Session id from session_open."),
                ["path"] = StringArg("The file's path in the workspace, e.g. out.png."),
                ["grant"] = StringArg("A write grant's name, from grants_list."),
                ["name"] = StringArg("File name in the grant folder (default: the source's name)."),
            },
            Required = ["session", "path", "grant"],
            ReadOnly = false, Destructive = false, Idempotent = false,
            Run = call => ExportAsync(sessions.Get(call.String("session")), call.String("path"), call.String("grant"), call.OptionalString("name"), call.CancellationToken),
        },
    ];

    private CallToolResult List(Session session)
    {
        using var _ = session.Use();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        var entries = new DirectoryInfo(session.WorkReal).EnumerateFiles("*", options).Take(MaxListed + 1).ToList();
        var files = new JsonArray([.. entries.Take(MaxListed).Select(f => (JsonNode?)new JsonObject
        {
            ["path"] = Path.GetRelativePath(session.WorkReal, f.FullName).Replace('\\', '/'),
            ["bytes"] = f.Length,
            ["modifiedUtc"] = f.LastWriteTimeUtc.ToString("O"),
        })]);
        var info = new JsonObject { ["files"] = files, ["truncated"] = entries.Count > MaxListed };
        var result = new CallToolResult { Content = [new TextContentBlock { Text = Json.Indented(info) }], IsError = false };
        return Untrusted.Capped(session.App.Display, Untrusted.Labeled(session.App.Display, session.Id, result), settings.MaxResponseMb);
    }

    /// <summary>
    /// The checks an import of <paramref name="path"/> from <paramref name="grantName"/> can make
    /// before a session exists (the grant, the spelling, the type), so <c>session_open</c> refuses a
    /// bad project without starting a sandbox.
    /// </summary>
    public void CheckImport(AppManifest app, string grantName, string path)
    {
        Grant(grantName, GrantAccess.Read);
        var relative = RelativePath.Check(path, out var error) ?? throw new GatewayRefusalException(error);
        var extension = Path.GetExtension(relative).TrimStart('.').ToLowerInvariant();
        if (!settings.ImportFor(app.Id, app.Import).Contains(extension) || FileTypes.NeverAllowed.Contains(extension))
            throw new GatewayRefusalException($"{app.Display} can't import .{extension} files.");
    }

    /// <summary>Copies a grant file into the workspace; also used for <c>session_open</c>'s project.</summary>
    public async Task<CallToolResult> ImportAsync(Session session, string grantName, string path, string? name, CancellationToken cancellationToken)
    {
        using var _ = session.Use();
        var grant = Grant(grantName, GrantAccess.Read);
        var maxBytes = (long)settings.MaxFileMbFor(session.App.Id, session.App.MaxFileMb) * 1024 * 1024;
        var rules = new FileRules(settings.ImportFor(session.App.Id, session.App.Import), maxBytes, _protectedPaths);
        await session.ImportLock.WaitAsync(cancellationToken);
        (string, long) result;
        try
        {
            var budget = maxBytes * WorkspaceCapFactor - FileTransfer.SizeOf(session.WorkReal);
            result = await TransferAsync(token => FileTransfer.Copy(grant.Path, path, session.WorkReal, name, rules, budget, token), cancellationToken);
        }
        finally
        {
            session.ImportLock.Release();
        }
        var (copied, bytes) = result;
        return ToolCatalog.Text(new JsonObject
        {
            ["imported"] = Path.GetFileName(copied),
            ["bytes"] = bytes,
            ["from"] = $"{grant.Name}/{path.Replace('\\', '/')}",
        });
    }

    private async Task<CallToolResult> ExportAsync(Session session, string path, string grantName, string? name, CancellationToken cancellationToken)
    {
        using var _ = session.Use();
        var grant = Grant(grantName, GrantAccess.Write);
        var maxBytes = (long)settings.MaxFileMbFor(session.App.Id, session.App.MaxFileMb) * 1024 * 1024;
        // The workspace sits inside the gateway's protected state folder by design; the source must
        // really be in the workspace, which FileTransfer checks on the open handle.
        var rules = new FileRules(settings.ExportFor(session.App.Id, session.App.Export), maxBytes, ProtectedPaths: []);
        var (copied, bytes) = await TransferAsync(token => FileTransfer.Copy(session.WorkReal, path, grant.Path, name, rules, cancellationToken: token), cancellationToken);
        return ToolCatalog.Text(new JsonObject
        {
            ["exported"] = copied,
            ["bytes"] = bytes,
            ["note"] = "Made by an untrusted program: open it with care.",
        });
    }

    private Grant Grant(string name, GrantAccess access)
    {
        var grant = settings.Grants.FirstOrDefault(g => g.Name == name);
        if (grant is null)
        {
            var names = settings.Grants.Where(g => g.Access == access).Select(g => g.Name).ToList();
            throw new GatewayRefusalException(names.Count == 0
                ? $"There are no {access.ToString().ToLowerInvariant()} grants; the user can add one with --grant-{access.ToString().ToLowerInvariant()} <name>=<folder> in the MCP server config."
                : $"No grant '{name}'. {access} grants: {string.Join(", ", names)}.");
        }
        if (grant.Access != access)
            throw new GatewayRefusalException($"The grant '{name}' is a {grant.Access.ToString().ToLowerInvariant()} grant, not a {access.ToString().ToLowerInvariant()} grant.");
        return grant;
    }

    // Copies run off the request thread, with --export-timeout on top of the client's cancellation.
    private async Task<(string Path, long Bytes)> TransferAsync(Func<CancellationToken, (string, long)> copy, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(settings.ExportTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await Task.Run(() => copy(linked.Token), linked.Token);
        }
        catch (FileTransferException ex)
        {
            throw new GatewayRefusalException(ex.Message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new GatewayRefusalException($"The copy didn't finish within --export-timeout ({settings.ExportTimeout.TotalSeconds:0} s); nothing was kept.");
        }
    }
}
