using SafeCraftMcp.Settings;
using Microsoft.Mxc.Sdk;
using Microsoft.Mxc.Sdk.V1;

namespace SafeCraftMcp.Sandbox;

/// <summary>What MXC can do on this host, for <c>doctor</c> and the tier gate.</summary>
public sealed record PlatformReport
{
    public required bool Supported { get; init; }
    public string? Reason { get; init; }
    public required string NativeVersion { get; init; }
    public required IReadOnlyList<string> Backends { get; init; }

    /// <summary>The ProcessContainer tier the probed request would get, or null when probing failed.</summary>
    public IsolationTier? Tier { get; init; }
    public bool? NeedsDaclAugmentation { get; init; }
    public string? ProbeError { get; init; }
}

/// <summary>
/// Decides whether a sandbox may start. Every spawn probes its own request and asks
/// <see cref="Check"/> first: with the default <c>--min-tier processisolation</c>, only MXC's
/// BaseContainer (Tier 1) without DACL augmentation passes. The AppContainer tiers rewrite DACLs
/// on host folders or need a broker, so they're refused unless the user opted in. The tier depends on the request (denied paths can push Tier 1 down to AppContainer on
/// hosts without native support), so <c>doctor</c> probes a request shaped like a session's.
/// </summary>
public static class TierGate
{
    public static PlatformReport Describe(ContainerRequest request)
    {
        try
        {
            var support = MxcPlatform.GetPlatformSupport();
            var report = new PlatformReport
            {
                Supported = support.IsSupported,
                Reason = support.Reason,
                NativeVersion = MxcPlatform.NativeVersion,
                Backends = MxcPlatform.GetAvailableBackends()
                    .Select(b => b.Tier is { } tier ? $"{b.Backend} ({tier})" : b.Backend.ToString())
                    .ToList(),
            };
            if (!support.IsSupported)
                return report;
            var probe = MxcContainer.Probe(request);
            return report with { Tier = probe.Tier, NeedsDaclAugmentation = probe.NeedsDaclAugmentation, ProbeError = probe.Error };
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException or MxcException)
        {
            // mxc_ffi.dll missing or not loadable, or the probe failed: report it, so doctor
            // explains instead of failing, and the gate refuses (no tier).
            return new PlatformReport { Supported = false, Reason = $"MXC didn't answer: {ex.Message}", NativeVersion = "unknown", Backends = [] };
        }
    }

    /// <summary>A request shaped like a session's for probing: ProcessContainer with denied paths.</summary>
    public static ContainerRequest ProbeRequest(IReadOnlyList<string> deniedPaths) => new("cmd.exe")
    {
        Containment = new Containment.ProcessContainer(),
        Filesystem = new FilesystemPolicy { DeniedPaths = [.. deniedPaths], ClearPolicyOnExit = true },
        Network = new NetworkPolicy
        {
            Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
        },
        Ui = new UiPolicy { Disable = true },
    };

    /// <summary>Null when the probed tier is allowed, else the reason it isn't, with what to do about it.</summary>
    public static string? Check(IsolationTier? tier, bool? needsDaclAugmentation, MinTier minTier) => (tier, minTier) switch
    {
        // An unknown DACL answer fails closed, like an unknown tier.
        (IsolationTier.BaseContainer, _) when needsDaclAugmentation is false => null,
        (IsolationTier.BaseContainer or IsolationTier.AppContainerBfs or IsolationTier.AppContainerDacl, MinTier.AppContainer) => null,
        (IsolationTier.BaseContainer or IsolationTier.AppContainerBfs or IsolationTier.AppContainerDacl, _) =>
            $"MXC would run the app at {tier}{(needsDaclAugmentation is true ? " with DACL changes on host folders" : "")}, and the gateway " +
            "allows only Process Isolation (BaseContainer) without them. Install the Windows cumulative update that adds Process Isolation " +
            "(Windows 11 26H2: KB5124010, build 26300.9550 or later), or start the gateway with --min-tier appcontainer after running wxc-host-prep.",
        _ => $"MXC reports no usable ProcessContainer tier ({(tier is null ? "none" : tier.ToString())}); the gateway can't sandbox apps on this host.",
    };
}
