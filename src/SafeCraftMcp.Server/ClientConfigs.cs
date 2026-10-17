using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Server;

/// <summary>A place where an MCP client registers this gateway.</summary>
public sealed record ClientRegistration(string Client, string File, string Scope, string Name, string? Command, IReadOnlyList<string> Args);

/// <summary>The registrations found, and the config files that exist but couldn't be read.</summary>
public sealed record ClientScan(IReadOnlyList<ClientRegistration> Registrations, IReadOnlyList<string> Problems);

/// <summary>
/// Reads the Claude clients' MCP server configs to report, in <c>doctor</c>, where the gateway is
/// registered and whether the registrations' options differ. Read-only: the gateway never writes a
/// client config, and the sandbox denies every one of them to the apps.
/// </summary>
public static class ClientConfigs
{
    public static ClientScan Find(HostPaths host)
    {
        var found = new List<ClientRegistration>();
        var problems = new List<string>();

        // Claude Code (CLI, VS Code extension, desktop Code tab) shares ~/.claude.json: user scope at
        // the top, local scope per project under "projects".
        var claudeJson = Path.Combine(host.UserProfile, ".claude.json");
        if (Read(claudeJson, problems) is { } code)
        {
            found.AddRange(Servers("Claude Code", claudeJson, "user", Member(code, "mcpServers")));
            if (Member(code, "projects") is JsonObject projects)
                foreach (var (project, node) in projects)
                    found.AddRange(Servers("Claude Code", claudeJson, $"local: {project}", Member(node, "mcpServers")));
        }

        // Claude Desktop: the classic install, and the Microsoft Store package's redirected folder.
        var desktopFiles = new List<string> { Path.Combine(host.AppData, "Claude", "claude_desktop_config.json") };
        var packages = Path.Combine(host.LocalAppData, "Packages");
        if (Directory.Exists(packages))
            desktopFiles.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                .Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json")));
        foreach (var file in desktopFiles)
            if (Read(file, problems) is { } desktop)
                found.AddRange(Servers("Claude Desktop", file, "user", Member(desktop, "mcpServers")));

        return new ClientScan(found, problems);
    }

    private static IEnumerable<ClientRegistration> Servers(string client, string file, string scope, JsonNode? servers)
    {
        if (servers is not JsonObject map)
            yield break;
        foreach (var (name, entry) in map)
        {
            var command = Member(entry, "command") is JsonValue c && c.TryGetValue<string>(out var s) ? s : null;
            var isGateway = name.Equals("safecraft", StringComparison.OrdinalIgnoreCase)
                || (command is not null && Path.GetFileNameWithoutExtension(command).Equals("safecraft-mcp", StringComparison.OrdinalIgnoreCase));
            if (!isGateway)
                continue;
            var args = Member(entry, "args") is JsonArray a
                ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var arg) ? arg : x?.ToJsonString() ?? "").ToList()
                : [];
            yield return new ClientRegistration(client, file, scope, name, command, args);
        }
    }

    // A hand-edited config may hold a string where an object belongs; the indexer would throw.
    private static JsonNode? Member(JsonNode? node, string name) => node is JsonObject o ? o[name] : null;

    private static JsonNode? Read(string file, List<string> problems)
    {
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"{file}: {ex.Message}");
            return null;
        }
    }
}
