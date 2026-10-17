using System.Text.Json;
using System.Text.RegularExpressions;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Registry;

/// <summary>
/// The apps compiled into the gateway. Loading checks every manifest against the rules no data
/// file may break: no bridge or connect flags (the sandboxed CLI must never drive a desktop app),
/// only known placeholders, and no never-allowed file types.
/// </summary>
public sealed partial class AppRegistry
{
    // Flag prefixes that make a CLI talk to a running desktop app, or listen, instead of using its
    // own headless engine: --bridge, --connect, --control(-token, -token-file), --port, ...
    private static readonly string[] NetworkFlagPrefixes = ["--bridge", "--connect", "--control", "--port", "--host", "--addr", "--listen", "--url"];

    // Apps whose CLI connects to the desktop app unless told otherwise (vectorcraft-cli mcp tries
    // 127.0.0.1:7979 first), and the flag that stops it.
    private static readonly Dictionary<string, string> RequiredFlags = new() { ["vectorcraft"] = "--headless" };

    private static readonly string[] Placeholders = ["{workspace}", "{profile}"];

    // Switches a manifest may pass without a value. Anything else after "mcp" must be a flag whose
    // value is a placeholder (a root or data folder the gateway controls).
    private static readonly HashSet<string> AllowedSwitches = ["--headless", "--compact"];

    // The gateway sets these for the sandbox; a manifest can't override them.
    private static readonly string[] ReservedEnv =
        ["PATH", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "COMSPEC", "PATHEXT"];

    public IReadOnlyList<AppManifest> Apps { get; }

    public AppRegistry(IReadOnlyList<AppManifest> apps)
    {
        foreach (var app in apps)
            Validate(app);
        if (apps.GroupBy(a => a.Id).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new InvalidDataException($"App '{duplicate.Key}' has two manifests.");
        Apps = apps;
    }

    public IReadOnlyList<string> Ids => Apps.Select(a => a.Id).ToList();

    public AppManifest? Find(string id) => Apps.FirstOrDefault(a => a.Id == id);

    /// <summary>The curated guide for an app, embedded from <c>guides/&lt;id&gt;.md</c>; null when it has none yet.</summary>
    public static string? Guide(string appId)
    {
        using var stream = typeof(AppRegistry).Assembly.GetManifestResourceStream($"guides/{appId}.md");
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The manifests embedded from the repo's <c>apps/</c> folder.</summary>
    public static AppRegistry LoadEmbedded()
    {
        var assembly = typeof(AppRegistry).Assembly;
        var apps = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("apps/", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return Parse(stream, name);
            })
            .ToList();
        return new AppRegistry(apps);
    }

    public static AppManifest Parse(Stream json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ManifestJsonContext.Default.AppManifest)
                ?? throw new InvalidDataException($"{source}: empty manifest.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }
    }

    private static void Validate(AppManifest app)
    {
        var problems = new List<string>();
        if (!IdPattern().IsMatch(app.Id))
            problems.Add("the id must be lowercase letters and digits");
        if (!BinaryPattern().IsMatch(app.Binary))
            problems.Add($"binary '{app.Binary}' must be a bare file name ending in -cli.exe");
        // A search folder without a placeholder would resolve from the current directory.
        if (app.Search.FirstOrDefault(s => !s.StartsWith("{repos}/", StringComparison.Ordinal) && !s.StartsWith("{ProgramFiles}/", StringComparison.Ordinal)) is { } search)
            problems.Add($"search folder '{search}' must start with {{repos}}/ or {{ProgramFiles}}/");
        if (app.Search.FirstOrDefault(s => s.Contains("..", StringComparison.Ordinal)) is { } up)
            problems.Add($"search folder '{up}' must not contain '..'");
        if (app.Args.FirstOrDefault(a => NetworkFlagPrefixes.Any(f => a.StartsWith(f, StringComparison.OrdinalIgnoreCase)) || HostPortPattern().IsMatch(a)) is { } bridge)
            problems.Add($"argument '{bridge}' would connect to a desktop app or the network");
        if (ArgsShapeProblem(app.Args) is { } shape)
            problems.Add(shape);
        foreach (var key in app.Env.Keys)
        {
            if (!EnvKeyPattern().IsMatch(key))
                problems.Add($"env key '{key}' must be uppercase letters, digits and '_'");
            else if (ReservedEnv.Contains(key) || key.StartsWith("XDG_", StringComparison.Ordinal))
                problems.Add($"env key '{key}' is set by the gateway");
        }
        if (app.Env.Values.FirstOrDefault(v => v.Contains("://", StringComparison.Ordinal) || HostPortPattern().IsMatch(v)) is { } address)
            problems.Add($"env value '{address}' looks like a network address");
        if (RequiredFlags.TryGetValue(app.Id, out var required) && !app.Args.Contains(required))
            problems.Add($"the arguments must include {required}");
        foreach (var arg in app.Args.Concat(app.Env.Values))
        {
            var rest = Placeholders.Aggregate(arg, (s, p) => s.Replace(p, "", StringComparison.Ordinal));
            if (rest.Contains('{') || rest.Contains('}'))
                problems.Add($"'{arg}' has an unknown placeholder");
        }
        foreach (var (list, name) in new[] { (app.Import, "import"), (app.Export, "export") })
        {
            if (list.FirstOrDefault(e => !FileTypes.IsWellFormed(e)) is { } bad)
                problems.Add($"{name} type '{bad}' isn't a plain lowercase extension");
            if (list.FirstOrDefault(FileTypes.NeverAllowed.Contains) is { } refused)
                problems.Add($"{name} type '{refused}' is never allowed");
        }
        if (app.MaxFileMb is < 1 or > SettingsParser.MaxFileMbCap)
            problems.Add($"maxFileMb {app.MaxFileMb} is outside 1..{SettingsParser.MaxFileMbCap}");
        if (app.CallTimeoutSeconds is < 1 or > SettingsParser.CallTimeoutCapSeconds)
            problems.Add($"callTimeoutSeconds {app.CallTimeoutSeconds} is outside 1..{SettingsParser.CallTimeoutCapSeconds}");
        if (problems.Count > 0)
            throw new InvalidDataException($"Manifest '{app.Id}': {string.Join("; ", problems)}.");
    }

    // "mcp", then switches from AllowedSwitches or "--flag <value with a placeholder>" pairs.
    private static string? ArgsShapeProblem(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "mcp")
            return "the arguments must start with 'mcp'";
        for (var i = 1; i < args.Count; i++)
        {
            if (AllowedSwitches.Contains(args[i]))
                continue;
            var hasPlaceholderValue = i + 1 < args.Count && Placeholders.Any(p => args[i + 1].Contains(p, StringComparison.Ordinal));
            if (!FlagPattern().IsMatch(args[i]) || !hasPlaceholderValue)
                return $"argument '{args[i]}' isn't an allowed switch or a flag with a {{workspace}}/{{profile}} value";
            i++;
        }
        return null;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9]*\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A--[a-z][a-z0-9-]*\z")]
    private static partial Regex FlagPattern();

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]*\z")]
    private static partial Regex EnvKeyPattern();

    [GeneratedRegex(@"\A[a-z0-9-]+-cli\.exe\z")]
    private static partial Regex BinaryPattern();

    // "127.0.0.1:7979", "localhost:9876", "[::1]:80": an address to connect to.
    [GeneratedRegex(@"\A(\[[0-9a-fA-F:]+\]|[A-Za-z0-9.-]+):[0-9]{1,5}\z")]
    private static partial Regex HostPortPattern();
}
