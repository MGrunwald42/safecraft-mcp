namespace SafeCraftMcp.Settings;

public enum MinTier { ProcessIsolation, AppContainer }

public enum GrantAccess { Read, Write }

/// <summary>A host folder files may be imported from (read) or exported to (write).</summary>
public sealed record Grant(string Name, string Path, GrantAccess Access);

/// <summary>
/// The gateway's security settings, fixed at startup. Every property's default is the most secure
/// value; only <see cref="SettingsParser"/> can relax one, from the command line.
/// Nothing at run time changes them: no MCP tool takes a setting.
/// </summary>
public sealed record GatewaySettings
{
    public MinTier MinTier { get; init; } = MinTier.ProcessIsolation;

    /// <summary>The apps that may be opened; null means every app with a found binary.</summary>
    public IReadOnlyList<string>? Apps { get; init; }

    /// <summary>Where to look for <c>&lt;app&gt;\target\release\&lt;app&gt;-cli.exe</c>.</summary>
    public required string Repos { get; init; }

    /// <summary>App id → the only SHA-256 (lowercase hex) of that app's binary allowed to run.</summary>
    public IReadOnlyDictionary<string, string> Pins { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<Grant> Grants { get; init; } = [];

    /// <summary>App id → replacement import extension list (lowercase, no dot).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ImportExtensions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>App id → replacement export extension list (lowercase, no dot).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExportExtensions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>App id → replacement per-file size cap in MB.</summary>
    public IReadOnlyDictionary<string, int> MaxFileMb { get; init; } = new Dictionary<string, int>();

    public bool MountGrants { get; init; }
    public bool KeepWorkspace { get; init; }
    public bool UpstreamDocs { get; init; }

    public int MaxSessions { get; init; } = 2;
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ExportTimeout { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(1);
    public int MaxResponseMb { get; init; } = 8;
    public int MaxPreviewSide { get; init; } = 1024;

    /// <summary>Memory a session's whole process tree may commit, in MB (a Job Object limit).</summary>
    public int MaxMemoryMb { get; init; } = 8192;

    /// <summary>How many processes a session may run at once (a Job Object limit).</summary>
    public int MaxProcesses { get; init; } = 16;

    /// <summary>The options given on the command line (without dashes), for <c>settings_show</c>.</summary>
    public IReadOnlySet<string> FromOptions { get; init; } = new HashSet<string>();

    public static GatewaySettings Defaults(HostPaths host) => new() { Repos = host.DefaultRepos };

    /// <summary>An app's import types: <c>--import-ext</c> when given for it, else the manifest's.</summary>
    public IReadOnlyList<string> ImportFor(string appId, IReadOnlyList<string> manifest) => ImportExtensions.GetValueOrDefault(appId) ?? manifest;

    /// <summary>An app's export types: <c>--export-ext</c> when given for it, else the manifest's.</summary>
    public IReadOnlyList<string> ExportFor(string appId, IReadOnlyList<string> manifest) => ExportExtensions.GetValueOrDefault(appId) ?? manifest;

    /// <summary>An app's per-file cap in MB: <c>--max-file-mb</c> when given for it, else the manifest's.</summary>
    public int MaxFileMbFor(string appId, int manifest) => MaxFileMb.TryGetValue(appId, out var mb) ? mb : manifest;

    /// <summary>An app's call timeout: <c>--call-timeout</c> when given, else the app manifest's value.</summary>
    public TimeSpan CallTimeoutFor(int manifestSeconds) =>
        FromOptions.Contains("call-timeout") ? CallTimeout : TimeSpan.FromSeconds(manifestSeconds);

    /// <summary>
    /// <see cref="HostPaths.Protected"/> plus the repos folder, wherever <c>--repos</c> puts it, each
    /// both as spelled and as its real path, so a protected folder behind a junction still matches
    /// a grant that names its target.
    /// </summary>
    public IReadOnlyList<string> ProtectedPaths(HostPaths host)
    {
        string[] literal = [.. host.Protected, Repos];
        return [.. literal.Concat(literal.Select(RealPath.ResolveAsFarAsExists)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
