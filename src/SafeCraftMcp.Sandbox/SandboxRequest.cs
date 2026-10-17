using SafeCraftMcp.Settings;
using Microsoft.Mxc.Sdk.V1;

namespace SafeCraftMcp.Sandbox;

/// <summary>What one session's sandbox is made of; everything else is fixed in <see cref="SandboxRequest.Build"/>.</summary>
public sealed record SandboxSpec
{
    /// <summary>The frozen copy of the app's CLI.</summary>
    public required string Executable { get; init; }

    /// <summary>The manifest's arguments, placeholders still in.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required SessionPaths Paths { get; init; }
    public required string ContainerName { get; init; }

    /// <summary>Protected folders, as MXC denied paths (defense in depth: nothing outside the grants is reachable anyway).</summary>
    public required IReadOnlyList<string> DeniedPaths { get; init; }

    /// <summary>Host folders mounted with <c>--mount-grants</c>; empty by default.</summary>
    public IReadOnlyList<Grant> MountedGrants { get; init; } = [];

    /// <summary>
    /// MXC kills the whole tree after this, a backstop for the session lifetime. MXC enforces it
    /// only while something waits on the process; <c>ChildConnection</c> waits from the start.
    /// </summary>
    public required TimeSpan Lifetime { get; init; }
}

/// <summary>
/// Builds the MXC request for a session. The child's command line comes only from
/// the manifest; the values the gateway fills in are the session's own folders. No network
/// (host loopback included, so a sandboxed CLI can't reach a desktop app's control port), no UI,
/// no clipboard, and an explicit environment with every user folder pointing into the profile.
/// </summary>
public static class SandboxRequest
{
    /// <summary>
    /// The protected paths MXC can take as denied paths. MXC fails the spawn with "path not found"
    /// for a denied path whose parent folder doesn't exist (a missing leaf is fine). Such a path
    /// can't be reached anyway: the sandbox can create folders only inside its own grants.
    /// </summary>
    public static IReadOnlyList<string> UsableDeniedPaths(IEnumerable<string> protectedPaths) =>
        [.. protectedPaths.Where(p => Path.GetDirectoryName(p) is { } parent && Directory.Exists(parent))];

    public static ContainerRequest Build(SandboxSpec spec)
    {
        var paths = spec.Paths;
        var arguments = spec.Arguments.Select(a => a
            .Replace("{workspace}", paths.Work, StringComparison.Ordinal)
            .Replace("{profile}", paths.Profile, StringComparison.Ordinal));
        var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        var environment = new Dictionary<string, string>(spec.Environment)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["PATH"] = Path.Combine(windows, "System32"),
            ["USERPROFILE"] = paths.Profile,
            ["HOME"] = paths.Profile,
            ["APPDATA"] = paths.Roaming,
            ["LOCALAPPDATA"] = paths.Local,
            ["TEMP"] = paths.Temp,
            ["TMP"] = paths.Temp,
            ["XDG_CONFIG_HOME"] = Path.Combine(paths.Profile, ".config"),
            ["XDG_DATA_HOME"] = Path.Combine(paths.Profile, ".local", "share"),
            ["XDG_STATE_HOME"] = Path.Combine(paths.Profile, ".local", "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(paths.Profile, ".cache"),
        };
        return new ContainerRequest(CommandLine.Build(spec.Executable, arguments))
        {
            Containment = new Containment.ProcessContainer(),
            ContainerName = spec.ContainerName,
            WorkingDirectory = paths.Work,
            Filesystem = new FilesystemPolicy
            {
                ReadonlyPaths = [Path.GetDirectoryName(spec.Executable)!, .. spec.MountedGrants.Where(g => g.Access == GrantAccess.Read).Select(g => g.Path)],
                ReadwritePaths = [paths.Work, paths.Profile, .. spec.MountedGrants.Where(g => g.Access == GrantAccess.Write).Select(g => g.Path)],
                DeniedPaths = [.. spec.DeniedPaths],
                ClearPolicyOnExit = true,
            },
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
            },
            Ui = new UiPolicy { Disable = true, Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
            TimeoutMs = (uint)Math.Min(spec.Lifetime.TotalMilliseconds, uint.MaxValue),
            Environment = environment,
            InheritDefaultEnvironment = false,
        };
    }
}
