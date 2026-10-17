using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Server;

/// <summary>
/// The read-only tools: <c>doctor</c>, <c>settings_show</c> and <c>crafts_list</c>. They report the
/// gateway's state so an agent can explain a refusal; none of them changes anything.
/// </summary>
/// <param name="describePlatform">Asks MXC about the host (tests pass a fixed report).</param>
public sealed class InfoTools(
    GatewaySettings settings, AppRegistry registry, BinaryResolver binaries, HostPaths host, IReadOnlyList<string> commandLine,
    Func<PlatformReport> describePlatform)
{
    private readonly Lazy<PlatformReport> _platform = new(describePlatform);

    private string? TierRefusal(PlatformReport platform) => platform.Supported
        ? TierGate.Check(platform.Tier, platform.NeedsDaclAugmentation, settings.MinTier)
        : platform.Reason ?? "MXC isn't supported on this host.";

    public IEnumerable<GatewayTool> Tools() =>
    [
        new()
        {
            Name = "doctor",
            Title = "Doctor",
            Description = "Checks the host: MXC platform support and the isolation tier a session would get, " +
                "whether the tier gate allows it, the app binaries found with their SHA-256, and which Claude " +
                "clients register this gateway with which options.",
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = _ => Task.FromResult(ToolCatalog.Text(Doctor())),
        },
        new()
        {
            Name = "settings_show",
            Title = "Show settings",
            Description = "The gateway's effective security settings, each with its source: the built-in default " +
                "(the most secure value) or a command-line option in the MCP server config. Read-only: settings " +
                "change only when the client restarts the gateway with other options.",
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = _ => Task.FromResult(ToolCatalog.Text(SettingsJson())),
        },
        new()
        {
            Name = "crafts_list",
            Title = "List apps",
            Description = "The ArtCraft apps this gateway knows: whether each binary was found, its version, repo " +
                "commit and SHA-256, whether it's pinned, and whether a session can be opened for it (and if not, why). " +
                "Version and commit come from the app's own files and are shown only when they look like a version or a hash.",
            ReadOnly = true, Destructive = false, Idempotent = true,
            Run = _ => Task.FromResult(ToolCatalog.Text(CraftsList())),
        },
    ];

    public JsonObject Doctor()
    {
        var platform = _platform.Value;
        var refusal = TierRefusal(platform);
        var scan = ClientConfigs.Find(host);
        return new JsonObject
        {
            ["os"] = Environment.OSVersion.VersionString,
            ["mxc"] = new JsonObject
            {
                ["supported"] = platform.Supported,
                ["reason"] = platform.Reason,
                ["nativeVersion"] = platform.NativeVersion,
                ["backends"] = Json.Array(platform.Backends),
                ["tier"] = platform.Tier?.ToString(),
                ["needsDaclAugmentation"] = platform.NeedsDaclAugmentation,
                ["probeError"] = platform.ProbeError,
            },
            ["minTier"] = settings.MinTier.ToString(),
            ["sessionsAllowed"] = refusal is null,
            ["refusal"] = refusal,
            ["binaries"] = new JsonArray([.. registry.Apps.Select(app => (JsonNode?)(binaries.Find(app) is { } found
                ? new JsonObject { ["app"] = app.Id, ["path"] = found.Path, ["sha256"] = found.Sha256, ["error"] = found.Error }
                : new JsonObject { ["app"] = app.Id, ["path"] = null }))]),
            ["clients"] = new JsonObject
            {
                ["thisProcessArgs"] = Json.Array(commandLine),
                ["registrations"] = new JsonArray([.. scan.Registrations.Select(r => (JsonNode?)new JsonObject
                {
                    ["client"] = r.Client,
                    ["file"] = r.File,
                    ["scope"] = r.Scope,
                    ["name"] = r.Name,
                    ["command"] = r.Command,
                    ["args"] = Json.Array(r.Args),
                    ["sameArgsAsThisProcess"] = r.Args.SequenceEqual(commandLine),
                })]),
                ["registrationsAgree"] = scan.Registrations.Select(r => string.Join('\u001f', r.Args)).Distinct().Count() <= 1,
                ["unreadableConfigs"] = Json.Array(scan.Problems),
            },
        };
    }

    public JsonObject SettingsJson()
    {
        JsonObject Entries(string[] options, JsonNode? value)
        {
            var given = options.Where(settings.FromOptions.Contains).Select(o => "--" + o).ToList();
            return new() { ["value"] = value, ["source"] = given.Count > 0 ? "option " + string.Join(", ", given) : "default" };
        }
        JsonObject Entry(string option, JsonNode? value) => Entries([option], value);
        // The effective per-app rules: the manifest's unless an option replaced them.
        JsonObject AppRules(AppManifest app)
        {
            JsonObject Rule(string option, JsonNode? value, bool fromOption) =>
                new() { ["value"] = value, ["source"] = fromOption ? $"option --{option}" : "default (app manifest)" };
            return new()
            {
                ["import"] = Rule("import-ext", Json.Array(settings.ImportFor(app.Id, app.Import)), settings.ImportExtensions.ContainsKey(app.Id)),
                ["export"] = Rule("export-ext", Json.Array(settings.ExportFor(app.Id, app.Export)), settings.ExportExtensions.ContainsKey(app.Id)),
                ["maxFileMb"] = Rule("max-file-mb", settings.MaxFileMbFor(app.Id, app.MaxFileMb), settings.MaxFileMb.ContainsKey(app.Id)),
                ["callTimeoutSeconds"] = Rule("call-timeout", (int)settings.CallTimeoutFor(app.CallTimeoutSeconds).TotalSeconds, settings.FromOptions.Contains("call-timeout")),
            };
        }

        return new JsonObject
        {
            ["minTier"] = Entry("min-tier", settings.MinTier.ToString().ToLowerInvariant()),
            ["apps"] = Entry("apps", settings.Apps is null ? null : Json.Array(settings.Apps)),
            ["appsNullMeans"] = "every app with a found binary",
            ["repos"] = Entry("repos", settings.Repos),
            ["pins"] = Entry("pin", new JsonObject(settings.Pins.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))),
            ["grants"] = Entries(["grant-read", "grant-write"],
                new JsonArray([.. settings.Grants.Select(g => (JsonNode?)new JsonObject
                {
                    ["name"] = g.Name, ["path"] = g.Path, ["access"] = g.Access.ToString().ToLowerInvariant(),
                })])),
            ["perApp"] = new JsonObject(registry.Apps.Select(app => KeyValuePair.Create(app.Id, (JsonNode?)AppRules(app)))),
            ["neverAllowedTypes"] = new JsonObject { ["value"] = Json.Array(FileTypes.NeverAllowed.Order()), ["source"] = "fixed (no option)" },
            ["mountGrants"] = Entry("mount-grants", settings.MountGrants),
            ["keepWorkspace"] = Entry("keep-workspace", settings.KeepWorkspace),
            ["upstreamDocs"] = Entry("upstream-docs", settings.UpstreamDocs),
            ["maxSessions"] = Entry("max-sessions", settings.MaxSessions),
            ["exportTimeoutSeconds"] = Entry("export-timeout", (int)settings.ExportTimeout.TotalSeconds),
            ["idleTimeoutSeconds"] = Entry("idle-timeout", (int)settings.IdleTimeout.TotalSeconds),
            ["maxLifetimeSeconds"] = Entry("max-lifetime", (int)settings.MaxLifetime.TotalSeconds),
            ["maxResponseMb"] = Entry("max-response-mb", settings.MaxResponseMb),
            ["maxPreviewSide"] = Entry("max-preview-side", settings.MaxPreviewSide),
            ["maxMemoryMb"] = Entry("max-memory-mb", settings.MaxMemoryMb),
            ["maxProcesses"] = Entry("max-processes", settings.MaxProcesses),
            ["network"] = new JsonObject { ["value"] = "deny all, host loopback included", ["source"] = "fixed (no option)" },
            ["ui"] = new JsonObject { ["value"] = "disabled, no clipboard, no input injection", ["source"] = "fixed (no option)" },
        };
    }

    public JsonArray CraftsList()
    {
        var platform = _platform.Value;
        var tierRefusal = TierRefusal(platform);
        return new JsonArray([.. registry.Apps.Select(app =>
        {
            var found = binaries.Find(app);
            settings.Pins.TryGetValue(app.Id, out var pin);
            var why = Refusal(app, found, pin) ?? tierRefusal;
            return (JsonNode?)new JsonObject
            {
                ["id"] = app.Id,
                ["display"] = app.Display,
                ["repo"] = app.Repo,
                ["found"] = found is not null,
                ["binary"] = found is null ? null : new JsonObject
                {
                    ["path"] = found.Path,
                    ["source"] = found.Source.ToString().ToLowerInvariant(),
                    ["version"] = found.Version,
                    ["repoCommit"] = found.RepoCommit,
                    ["sha256"] = found.Sha256,
                    ["error"] = found.Error,
                    ["size"] = found.Size,
                    ["lastWriteUtc"] = found.LastWriteUtc.ToString("O"),
                },
                ["pinned"] = pin,
                ["pinMatches"] = pin is null || found is null ? null : pin == found.Sha256,
                ["tier"] = platform.Tier?.ToString(),
                ["canOpen"] = why is null,
                ["reason"] = why,
            };
        })]);
    }

    private string? Refusal(AppManifest app, FoundBinary? found, string? pin)
    {
        if (settings.Apps is not null && !settings.Apps.Contains(app.Id))
            return $"Not in --apps ({string.Join(", ", settings.Apps)}).";
        if (!app.Ready)
            return $"{app.Display} hasn't had its sandbox check yet.";
        if (found is null)
            return $"No {app.Binary} found in {string.Join(" or ", app.Search)} ({{repos}} = {settings.Repos}). Build it with update-craft.";
        if (found.Sha256 is null)
            return found.Error;
        if (pin is not null && pin != found.Sha256)
            return $"The found binary's SHA-256 {found.Sha256} isn't the pinned {pin}.";
        return null;
    }
}
