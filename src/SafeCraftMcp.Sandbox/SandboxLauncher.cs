using System.ComponentModel;
using SafeCraftMcp.Settings;
using Microsoft.Mxc.Sdk;
using Microsoft.Win32.SafeHandles;
using Microsoft.Mxc.Sdk.V1;

namespace SafeCraftMcp.Sandbox;

/// <summary>The sandbox refused to start; the message says why and what to do.</summary>
public sealed class SandboxRefusedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A started sandbox: the process, the tier it got, and the Job Object holding its limits.</summary>
public sealed record Sandboxed(MxcProcess Process, IsolationTier Tier, SafeFileHandle Job);

/// <summary>
/// The only way the gateway starts an app: probe this exact request, pass the tier gate, spawn
/// in a ProcessContainer, put it into a Job Object with the resource limits. There's no code path
/// that runs an app outside MXC, and none that turns on MXC's learning or audit mode. The caller
/// owns the job handle: closing it kills the sandbox's whole process tree.
/// </summary>
public static class SandboxLauncher
{
    public static async Task<Sandboxed> SpawnAsync(
        ContainerRequest request, MinTier minTier, ResourceLimits limits, CancellationToken cancellationToken = default)
    {
        if (request.Containment is not Containment.ProcessContainer { LearningMode: false, CaptureDenials: null })
            throw new SandboxRefusedException("Only a ProcessContainer without learning or capture mode may be spawned.");
        ProbeOutput probe;
        try
        {
            probe = MxcContainer.Probe(request);
        }
        catch (Exception ex) when (IsMxcFailure(ex))
        {
            throw new SandboxRefusedException($"MXC couldn't probe the sandbox: {Describe(ex)}", ex);
        }
        if (TierGate.Check(probe.Tier, probe.NeedsDaclAugmentation, minTier) is { } refusal)
            throw new SandboxRefusedException(refusal);
        MxcProcess process;
        try
        {
            process = await MxcContainer.SpawnAsync(request, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (IsMxcFailure(ex))
        {
            throw new SandboxRefusedException($"MXC couldn't start the sandbox: {Describe(ex)}", ex);
        }
        try
        {
            return new Sandboxed(process, probe.Tier!.Value, JobLimits.Apply(process.Id, limits));
        }
        catch (Win32Exception ex)
        {
            // No limits, no session: kill what was started.
            try
            {
                process.Kill();
            }
            catch (MxcException)
            {
                // Already gone.
            }
            process.Dispose();
            throw new SandboxRefusedException($"The sandbox couldn't be given its resource limits: {ex.Message}", ex);
        }
    }

    // An MXC error, or mxc_ffi.dll missing or not loadable (as TierGate.Describe sees it).
    private static bool IsMxcFailure(Exception ex) =>
        ex is MxcException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException;

    private static string Describe(Exception ex) => ex is MxcException mxc ? $"{mxc.Code}: {mxc.Message}" : ex.Message;
}
