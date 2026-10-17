using System.CommandLine;
using System.Text.RegularExpressions;

namespace SafeCraftMcp.Settings;

/// <summary>The command line was wrong; the gateway must not start.</summary>
public sealed class SettingsException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Resolves a folder option to its real path, or returns null with the reason.</summary>
public delegate string? FolderResolver(string path, out string error);

/// <summary>
/// Turns the gateway's command line into <see cref="GatewaySettings"/>. This is the only place a
/// security setting can be relaxed. Unknown options, malformed values, duplicates and values beyond
/// the hard caps are errors, never a guess; environment variables and config files aren't read,
/// and neither are response files (<c>@file</c>), which would be a config file resolved from the
/// current directory.
/// </summary>
public static partial class SettingsParser
{
    // Hard caps: the most an option may raise a limit to. They bound what a typo in a client config
    // can do (--max-sessions 1000), not what a careful user might want; raise them in code if needed.
    public const int MaxSessionsCap = 8;
    public const int CallTimeoutCapSeconds = 1800;
    public const int ExportTimeoutCapSeconds = 7200;
    public const int IdleTimeoutCapSeconds = 86_400;
    public const int MaxLifetimeCapSeconds = 86_400;
    public const int MaxResponseMbCap = 64;
    // PhotoCraft's render_preview refuses a max_side above 2048 (photocraft docs/mcp.md).
    public const int MaxPreviewSideCap = 2048;
    public const int MinPreviewSide = 64;
    // FilmCraft's default is 16 GB; four times that still catches a unit mix-up (MB vs KB).
    public const int MaxFileMbCap = 65_536;
    // A session's memory: at least enough to start an app, at most 128 GB.
    public const int MinMemoryMb = 256;
    public const int MaxMemoryMbCap = 131_072;
    public const int MaxProcessesCap = 256;

    private static readonly Option<string> MinTierOption = new("--min-tier")
    {
        Description = "Weakest isolation tier allowed: processisolation (default) or appcontainer (needs wxc-host-prep).",
    };
    private static readonly Option<string> AppsOption = new("--apps")
    {
        Description = "Comma-separated apps that may be opened (default: every app with a found binary).",
    };
    private static readonly Option<string> ReposOption = new("--repos")
    {
        Description = "Folder holding the app repositories (default: %USERPROFILE%\\Documents\\GitHub).",
    };
    private static readonly Option<string[]> PinOption = Repeatable("--pin", "<app>=<sha256>: only this build of the app runs.");
    private static readonly Option<string[]> GrantReadOption = Repeatable("--grant-read", "<name>=<dir>: a folder files may be imported from.");
    private static readonly Option<string[]> GrantWriteOption = Repeatable("--grant-write", "<name>=<dir>: a folder files may be exported to.");
    private static readonly Option<string[]> ImportExtOption = Repeatable("--import-ext", "<app>=<ext,ext,...>: replaces the app's import types.");
    private static readonly Option<string[]> ExportExtOption = Repeatable("--export-ext", "<app>=<ext,ext,...>: replaces the app's export types.");
    private static readonly Option<string[]> MaxFileMbOption = Repeatable("--max-file-mb", $"<app>=<n>: per-file size cap in MB (at most {MaxFileMbCap}).");
    private static readonly Option<bool> MountGrantsOption = new("--mount-grants")
    {
        Description = "Mount read grants read-only and write grants read-write into the sandbox.",
    };
    private static readonly Option<bool> KeepWorkspaceOption = new("--keep-workspace")
    {
        Description = "Keep session workspaces after the session closes.",
    };
    private static readonly Option<bool> UpstreamDocsOption = new("--upstream-docs")
    {
        Description = "Serve the apps' raw upstream docs as resources.",
    };
    private static readonly Option<int?> MaxSessionsOption = Limit("--max-sessions", $"Concurrent sessions (default 2, at most {MaxSessionsCap}).");
    private static readonly Option<int?> CallTimeoutOption = Limit("--call-timeout", $"Seconds per tool call (default 60, at most {CallTimeoutCapSeconds}).");
    private static readonly Option<int?> ExportTimeoutOption = Limit("--export-timeout", $"Seconds per export or render call (default 300, at most {ExportTimeoutCapSeconds}).");
    private static readonly Option<int?> IdleTimeoutOption = Limit("--idle-timeout", $"Seconds an unused session stays open (default 600, at most {IdleTimeoutCapSeconds}).");
    private static readonly Option<int?> MaxLifetimeOption = Limit("--max-lifetime", $"Seconds a session may live (default 3600, at most {MaxLifetimeCapSeconds}).");
    private static readonly Option<int?> MaxResponseMbOption = Limit("--max-response-mb", $"Largest tool response in MB (default 8, at most {MaxResponseMbCap}).");
    private static readonly Option<int?> MaxPreviewSideOption = Limit("--max-preview-side", $"Largest preview side in pixels (default 1024, {MinPreviewSide}..{MaxPreviewSideCap}).");
    private static readonly Option<int?> MaxMemoryMbOption = Limit("--max-memory-mb", $"Memory a session may commit, in MB (default 8192, {MinMemoryMb}..{MaxMemoryMbCap}).");
    private static readonly Option<int?> MaxProcessesOption = Limit("--max-processes", $"Processes a session may run at once (default 16, at most {MaxProcessesCap}).");

    /// <summary>The root command, also used to print <c>--help</c>.</summary>
    public static RootCommand Command { get; } = BuildCommand();

    /// <summary>Response files off: "@opts.rsp" would read settings from a file in the current directory.</summary>
    public static ParserConfiguration Configuration { get; } = new() { ResponseFileTokenReplacer = null };

    private static RootCommand BuildCommand()
    {
        var root = new RootCommand(
            "safecraft-mcp: an MCP gateway that runs the ArtCraft apps' CLIs inside MXC sandboxes. " +
            "Every default is the most secure value; these options relax it.");
        foreach (var option in AllOptions())
            root.Options.Add(option);
        return root;
    }

    private static IEnumerable<Option> AllOptions() =>
    [
        MinTierOption, AppsOption, ReposOption, PinOption, GrantReadOption, GrantWriteOption,
        ImportExtOption, ExportExtOption, MaxFileMbOption, MountGrantsOption, KeepWorkspaceOption,
        UpstreamDocsOption, MaxSessionsOption, CallTimeoutOption, ExportTimeoutOption,
        IdleTimeoutOption, MaxLifetimeOption, MaxResponseMbOption, MaxPreviewSideOption, MaxMemoryMbOption,
        MaxProcessesOption,
    ];

    public static ParseResult ParseCommandLine(IReadOnlyList<string> args) => Command.Parse(args, Configuration);

    /// <summary>True when the command line asks for help or the version instead of a server.</summary>
    public static bool IsInformational(ParseResult result) => result.Action is not null && result.Errors.Count == 0;

    /// <summary>Parses and validates; throws <see cref="SettingsException"/> listing every problem.</summary>
    /// <param name="knownApps">The app ids the embedded manifests define.</param>
    /// <param name="resolveFolder">Resolves a folder to its real path (tests pass a fake); default <see cref="RealPath.ResolveFolder"/>.</param>
    public static GatewaySettings Parse(
        IReadOnlyList<string> args, IReadOnlyCollection<string> knownApps, HostPaths host, FolderResolver? resolveFolder = null) =>
        FromParseResult(ParseCommandLine(args), knownApps, host, resolveFolder ?? RealPath.ResolveFolder);

    public static GatewaySettings FromParseResult(
        ParseResult result, IReadOnlyCollection<string> knownApps, HostPaths host, FolderResolver resolveFolder)
    {
        if (result.Errors.Count > 0)
            throw new SettingsException([.. result.Errors.Select(e => e.Message)]);

        var errors = new List<string>();
        var given = AllOptions()
            .Where(o => result.GetResult(o) is { Implicit: false })
            .Select(o => o.Name.TrimStart('-'))
            .ToHashSet();
        var settings = GatewaySettings.Defaults(host) with { FromOptions = given };

        if (result.GetValue(MinTierOption) is { } tier)
        {
            if (tier.Equals("processisolation", StringComparison.OrdinalIgnoreCase))
                settings = settings with { MinTier = MinTier.ProcessIsolation };
            else if (tier.Equals("appcontainer", StringComparison.OrdinalIgnoreCase))
                settings = settings with { MinTier = MinTier.AppContainer };
            else
                errors.Add($"--min-tier: '{tier}' isn't processisolation or appcontainer.");
        }

        if (result.GetValue(AppsOption) is { } apps)
        {
            var list = apps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.ToLowerInvariant()).ToList();
            if (list.Count == 0)
                errors.Add("--apps: no app named. Leave the option out to allow every app.");
            foreach (var app in list.Where(a => !knownApps.Contains(a)))
                errors.Add($"--apps: unknown app '{app}'. Known: {string.Join(", ", knownApps)}.");
            foreach (var app in list.GroupBy(a => a).Where(g => g.Count() > 1))
                errors.Add($"--apps: '{app.Key}' is named twice.");
            settings = settings with { Apps = list.Distinct().ToList() };
        }

        if (result.GetValue(ReposOption) is { } repos)
            settings = settings with { Repos = Folder(errors, "--repos", repos, resolveFolder, protectedPaths: null) ?? repos };

        // Grants are checked against the protected folders and the repos folder chosen above.
        var protectedPaths = settings.ProtectedPaths(host);
        settings = settings with
        {
            Pins = PerApp<string>(errors, result.GetValue(PinOption), "--pin", knownApps, TryPin),
            ImportExtensions = PerApp<IReadOnlyList<string>>(errors, result.GetValue(ImportExtOption), "--import-ext", knownApps, TryExtensions),
            ExportExtensions = PerApp<IReadOnlyList<string>>(errors, result.GetValue(ExportExtOption), "--export-ext", knownApps, TryExtensions),
            MaxFileMb = PerApp<int>(errors, result.GetValue(MaxFileMbOption), "--max-file-mb", knownApps, TryMegabytes),
            Grants =
            [
                .. Grants(errors, result.GetValue(GrantReadOption), "--grant-read", GrantAccess.Read, resolveFolder, protectedPaths),
                .. Grants(errors, result.GetValue(GrantWriteOption), "--grant-write", GrantAccess.Write, resolveFolder, protectedPaths),
            ],
            MountGrants = result.GetValue(MountGrantsOption),
            KeepWorkspace = result.GetValue(KeepWorkspaceOption),
            UpstreamDocs = result.GetValue(UpstreamDocsOption),
            MaxSessions = Bounded(errors, result.GetValue(MaxSessionsOption), "--max-sessions", 1, MaxSessionsCap) ?? settings.MaxSessions,
            CallTimeout = Seconds(errors, result.GetValue(CallTimeoutOption), "--call-timeout", CallTimeoutCapSeconds) ?? settings.CallTimeout,
            ExportTimeout = Seconds(errors, result.GetValue(ExportTimeoutOption), "--export-timeout", ExportTimeoutCapSeconds) ?? settings.ExportTimeout,
            IdleTimeout = Seconds(errors, result.GetValue(IdleTimeoutOption), "--idle-timeout", IdleTimeoutCapSeconds) ?? settings.IdleTimeout,
            MaxLifetime = Seconds(errors, result.GetValue(MaxLifetimeOption), "--max-lifetime", MaxLifetimeCapSeconds) ?? settings.MaxLifetime,
            MaxResponseMb = Bounded(errors, result.GetValue(MaxResponseMbOption), "--max-response-mb", 1, MaxResponseMbCap) ?? settings.MaxResponseMb,
            MaxPreviewSide = Bounded(errors, result.GetValue(MaxPreviewSideOption), "--max-preview-side", MinPreviewSide, MaxPreviewSideCap) ?? settings.MaxPreviewSide,
            MaxMemoryMb = Bounded(errors, result.GetValue(MaxMemoryMbOption), "--max-memory-mb", MinMemoryMb, MaxMemoryMbCap) ?? settings.MaxMemoryMb,
            MaxProcesses = Bounded(errors, result.GetValue(MaxProcessesOption), "--max-processes", 1, MaxProcessesCap) ?? settings.MaxProcesses,
        };

        foreach (var name in settings.Grants.GroupBy(g => g.Name).Where(g => g.Count() > 1).Select(g => g.Key))
            errors.Add($"Grant name '{name}' is used twice; names are unique across --grant-read and --grant-write.");

        if (errors.Count > 0)
            throw new SettingsException(errors);
        return settings;
    }

    private static Option<string[]> Repeatable(string name, string description) => new(name)
    {
        Description = description + " Repeatable.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = false,
    };

    private static Option<int?> Limit(string name, string description) => new(name) { Description = description };

    private delegate bool TryParse<T>(string value, out T result, out string error);

    private static bool TryPin(string value, out string result, out string error)
    {
        result = value.ToLowerInvariant();
        error = Sha256Pattern().IsMatch(value) ? "" : $"'{value}' isn't a SHA-256 (64 hex digits)";
        return error.Length == 0;
    }

    private static bool TryMegabytes(string value, out int result, out string error)
    {
        error = int.TryParse(value, out result) && result is >= 1 and <= MaxFileMbCap ? "" : $"'{value}' isn't a number of MB from 1 to {MaxFileMbCap}";
        return error.Length == 0;
    }

    private static bool TryExtensions(string value, out IReadOnlyList<string> result, out string error)
    {
        var list = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant()).Distinct().ToList();
        result = list;
        error = list.Count == 0 ? "no extension listed"
            : list.FirstOrDefault(e => !FileTypes.IsWellFormed(e)) is { } bad ? $"'{bad}' isn't a plain extension (letters and digits)"
            : list.FirstOrDefault(FileTypes.NeverAllowed.Contains) is { } refused ? $"'{refused}' is never allowed (executables, scripts, shortcuts, keys and certificates)"
            : "";
        return error.Length == 0;
    }

    private static Dictionary<string, T> PerApp<T>(
        List<string> errors, string[]? values, string option, IReadOnlyCollection<string> knownApps, TryParse<T> parse)
    {
        var map = new Dictionary<string, T>();
        foreach (var raw in values ?? [])
        {
            if (Split(raw) is not var (key, value))
            {
                errors.Add($"{option}: '{raw}' isn't <app>=<value>.");
                continue;
            }
            var app = key.ToLowerInvariant();
            if (!knownApps.Contains(app))
                errors.Add($"{option}: unknown app '{app}'. Known: {string.Join(", ", knownApps)}.");
            else if (map.ContainsKey(app))
                errors.Add($"{option}: '{app}' is given twice.");
            else if (parse(value, out var parsed, out var error))
                map[app] = parsed;
            else
                errors.Add($"{option} {app}: {error}.");
        }
        return map;
    }

    private static IEnumerable<Grant> Grants(
        List<string> errors, string[]? values, string option, GrantAccess access, FolderResolver resolveFolder, IReadOnlyList<string> protectedPaths)
    {
        foreach (var raw in values ?? [])
        {
            if (Split(raw) is not var (name, dir))
                errors.Add($"{option}: '{raw}' isn't <name>=<dir>.");
            else if (!GrantNamePattern().IsMatch(name))
                errors.Add($"{option}: grant name '{name}' must be lowercase letters, digits, '-' or '_', starting with a letter (at most 32).");
            else if (Folder(errors, $"{option} {name}", dir, resolveFolder, protectedPaths) is { } real)
                yield return new Grant(name, real, access);
        }
    }

    // The real path (links, junctions and short names resolved) of an existing local folder. A
    // grant must not be, contain or sit inside a protected folder, checked on the real path, so
    // a junction to .ssh named "out" is refused too.
    private static string? Folder(List<string> errors, string option, string dir, FolderResolver resolveFolder, IReadOnlyList<string>? protectedPaths)
    {
        var real = resolveFolder(dir, out var error);
        if (real is null)
        {
            errors.Add($"{option}: {error}");
            return null;
        }
        if (protectedPaths is not null && HostPaths.Overlaps(real, protectedPaths))
        {
            errors.Add($"{option}: '{real}' is or contains a protected folder (credentials, repos, client configs or the gateway's state). Grant a dedicated folder instead.");
            return null;
        }
        return real;
    }

    private static (string Key, string Value)? Split(string raw)
    {
        var at = raw.IndexOf('=');
        if (at <= 0 || at == raw.Length - 1)
            return null;
        return (raw[..at].Trim(), raw[(at + 1)..].Trim());
    }

    private static int? Bounded(List<string> errors, int? value, string option, int min, int max)
    {
        if (value is null)
            return null;
        if (value < min || value > max)
        {
            errors.Add($"{option}: {value} is outside {min}..{max}.");
            return null;
        }
        return value;
    }

    private static TimeSpan? Seconds(List<string> errors, int? value, string option, int max) =>
        Bounded(errors, value, option, 1, max) is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    [GeneratedRegex(@"\A[0-9a-fA-F]{64}\z")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,31}\z")]
    private static partial Regex GrantNamePattern();
}
