using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Settings;
using Microsoft.Mxc.Sdk.V1;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.IntegrationTests;

/// <summary>
/// A test that needs MXC Process Isolation and a built photocraft-cli.exe; on any other host it's
/// skipped with the reason. Tag: Category=Mxc.
/// </summary>
public sealed class MxcFactAttribute : FactAttribute
{
    public MxcFactAttribute()
    {
        if (MxcHost.SkipReason is { } reason)
            Skip = reason;
    }
}

/// <summary>The theory form of <see cref="MxcFactAttribute"/>.</summary>
public sealed class MxcTheoryAttribute : TheoryAttribute
{
    public MxcTheoryAttribute()
    {
        if (MxcHost.SkipReason is { } reason)
            Skip = reason;
    }
}

/// <summary>A test of one app's sessions: also skipped when that app's CLI isn't built.</summary>
public sealed class MxcAppFactAttribute : FactAttribute
{
    public MxcAppFactAttribute(string app)
    {
        if (MxcHost.SkipReason is { } reason)
            Skip = reason;
        else if (new BinaryResolver(GatewaySettings.Defaults(HostPaths.Current())).Find(AppRegistry.LoadEmbedded().Find(app)!) is null)
            Skip = $"No {app}-cli.exe built; build it with update-craft.";
    }
}

/// <summary>What the Mxc tests run against: this host's MXC and the user's built PhotoCraft CLI.</summary>
public static class MxcHost
{
    private static readonly Lazy<string?> s_skipReason = new(() =>
    {
        var real = HostPaths.Current();
        var platform = TierGate.Describe(TierGate.ProbeRequest([]));
        if (platform.Tier != IsolationTier.BaseContainer)
            return $"MXC Process Isolation isn't available here ({platform.Tier?.ToString() ?? platform.Reason ?? "no tier"}).";
        var photocraft = AppRegistry.LoadEmbedded().Find("photocraft")!;
        return new BinaryResolver(GatewaySettings.Defaults(real)).Find(photocraft) is null
            ? $"No photocraft-cli.exe under {real.DefaultRepos}; build it with update-craft."
            : null;
    });

    public static string? SkipReason => s_skipReason.Value;

    /// <summary>
    /// The real user folders, but the gateway's state (frozen binaries) and the sessions in a temp
    /// folder, so tests don't touch the installed gateway's state.
    /// </summary>
    public static HostPaths TestHost(string localAppData)
    {
        var real = HostPaths.Current();
        return new HostPaths(real.UserProfile, real.AppData, localAppData, real.Documents, temp: localAppData);
    }

    /// <summary>A tool result's text blocks, one per line.</summary>
    public static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>
    /// Deletes a test's temp folder, frozen binaries (read-only) included. A sandbox that was just
    /// killed can hold its exe for a moment longer, so a locked file is retried for a few seconds.
    /// </summary>
    public static void DeleteFolder(string folder)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }
}
