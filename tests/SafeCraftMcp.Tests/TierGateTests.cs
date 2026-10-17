using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Settings;
using Microsoft.Mxc.Sdk.V1;

namespace SafeCraftMcp.Tests;

public sealed class TierGateTests
{
    [Theory]
    [InlineData(IsolationTier.BaseContainer, false, MinTier.ProcessIsolation, true)]
    [InlineData(IsolationTier.BaseContainer, null, MinTier.ProcessIsolation, false)]
    [InlineData(IsolationTier.BaseContainer, true, MinTier.ProcessIsolation, false)]
    [InlineData(IsolationTier.BaseContainer, true, MinTier.AppContainer, true)]
    [InlineData(IsolationTier.AppContainerBfs, false, MinTier.ProcessIsolation, false)]
    [InlineData(IsolationTier.AppContainerDacl, true, MinTier.ProcessIsolation, false)]
    [InlineData(IsolationTier.AppContainerBfs, false, MinTier.AppContainer, true)]
    [InlineData(IsolationTier.AppContainerDacl, true, MinTier.AppContainer, true)]
    [InlineData(IsolationTier.Unknown, false, MinTier.AppContainer, false)]
    [InlineData(null, null, MinTier.AppContainer, false)]
    public void Only_Process_Isolation_Passes_By_Default(IsolationTier? tier, bool? dacl, MinTier minTier, bool allowed)
    {
        var refusal = TierGate.Check(tier, dacl, minTier);

        Assert.Equal(allowed, refusal is null);
    }

    [Fact]
    public void A_Refusal_Says_What_To_Do()
    {
        var refusal = TierGate.Check(IsolationTier.AppContainerDacl, true, MinTier.ProcessIsolation);

        Assert.Contains("KB5124010", refusal);
        Assert.Contains("--min-tier appcontainer", refusal);
    }

    [Fact]
    public void The_Probe_Request_Looks_Like_A_Session()
    {
        var request = TierGate.ProbeRequest([@"C:\Users\me\.ssh"]);

        Assert.IsType<Containment.ProcessContainer>(request.Containment);
        Assert.Equal([@"C:\Users\me\.ssh"], request.Filesystem!.DeniedPaths);
        Assert.Equal(NetworkAction.Deny, request.Network!.Ingress!.HostLoopback);
    }
}
