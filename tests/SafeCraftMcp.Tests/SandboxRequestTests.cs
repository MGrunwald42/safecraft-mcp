using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Settings;
using Microsoft.Mxc.Sdk.V1;

namespace SafeCraftMcp.Tests;

public sealed class SandboxRequestTests
{
    private static readonly SessionPaths Paths = new(@"C:\Users\me\AppData\Local\safecraft-mcp\sessions\42-s1");

    private static SandboxSpec Spec(IReadOnlyList<Grant>? mounted = null) => new()
    {
        Executable = @"C:\Users\me\AppData\Local\safecraft-mcp\bin\photocraft\abc\photocraft-cli.exe",
        Arguments = ["mcp", "--automation-read-root", "{workspace}", "--data-dir", "{profile}"],
        Environment = new Dictionary<string, string> { ["PHOTOCRAFT_X"] = "1" },
        Paths = Paths,
        ContainerName = "safecraft-mcp-42-s1",
        DeniedPaths = [@"C:\Users\me\.ssh"],
        MountedGrants = mounted ?? [],
        Lifetime = TimeSpan.FromHours(1),
    };

    [Fact]
    public void The_Command_Line_Comes_From_The_Manifest_With_The_Session_Folders_Filled_In()
    {
        var request = SandboxRequest.Build(Spec());

        Assert.Equal(
            "\"C:\\Users\\me\\AppData\\Local\\safecraft-mcp\\bin\\photocraft\\abc\\photocraft-cli.exe\" mcp --automation-read-root " +
            Paths.Work + " --data-dir " + Paths.Profile,
            request.Command);
        Assert.Equal(Paths.Work, request.WorkingDirectory);
    }

    [Fact]
    public void Only_The_Binary_Folder_Is_Read_Only_And_Only_The_Session_Folders_Are_Writable()
    {
        var filesystem = SandboxRequest.Build(Spec()).Filesystem!;

        Assert.Equal([@"C:\Users\me\AppData\Local\safecraft-mcp\bin\photocraft\abc"], filesystem.ReadonlyPaths);
        Assert.Equal([Paths.Work, Paths.Profile], filesystem.ReadwritePaths);
        Assert.Equal([@"C:\Users\me\.ssh"], filesystem.DeniedPaths);
        Assert.True(filesystem.ClearPolicyOnExit);
    }

    [Fact]
    public void Grants_Are_Mounted_Only_When_Asked()
    {
        var filesystem = SandboxRequest.Build(Spec([new Grant("in", @"D:\In", GrantAccess.Read), new Grant("out", @"D:\Out", GrantAccess.Write)])).Filesystem!;

        Assert.Contains(@"D:\In", filesystem.ReadonlyPaths);
        Assert.Contains(@"D:\Out", filesystem.ReadwritePaths);
        Assert.DoesNotContain(@"D:\In", filesystem.ReadwritePaths);
    }

    [Fact]
    public void No_Network_No_Loopback_No_Ui()
    {
        var request = SandboxRequest.Build(Spec());

        Assert.Equal(NetworkAction.Deny, request.Network!.Egress!.Default);
        Assert.Equal(NetworkAction.Deny, request.Network.Ingress!.Default);
        Assert.Equal(NetworkAction.Deny, request.Network.Ingress.HostLoopback);
        Assert.True(request.Ui!.Disable);
        Assert.Equal(ClipboardPolicy.None, request.Ui.Clipboard);
        Assert.False(request.Ui.AllowInputInjection);
        Assert.IsType<Containment.ProcessContainer>(request.Containment);
        Assert.Equal(3_600_000u, request.TimeoutMs);
    }

    [Fact]
    public void The_Environment_Is_Explicit_And_Points_Into_The_Profile()
    {
        var request = SandboxRequest.Build(Spec());
        var env = request.Environment!;

        Assert.False(request.InheritDefaultEnvironment);
        Assert.Equal("1", env["PHOTOCRAFT_X"]);
        foreach (var key in new[] { "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME" })
            Assert.StartsWith(Paths.Profile, env[key]);
        Assert.EndsWith(@"\System32", env["PATH"]);
        Assert.DoesNotContain(";", env["PATH"]);
    }

    [Fact]
    public void Denied_Paths_Without_An_Existing_Parent_Are_Left_Out()
    {
        // MXC fails the spawn for those (docs/sandbox-notes.md); a missing leaf is fine.
        var temp = Path.GetTempPath();
        string[] paths = [Path.Combine(temp, "missing-leaf"), Path.Combine(temp, "missing-parent", "leaf")];

        Assert.Equal([paths[0]], SandboxRequest.UsableDeniedPaths(paths));
    }

    [Fact]
    public async Task The_Launcher_Refuses_Learning_And_Capture_Modes_Before_Asking_Mxc()
    {
        var learning = SandboxRequest.Build(Spec());
        learning.Containment = new Containment.ProcessContainer { LearningMode = true };
        var capture = SandboxRequest.Build(Spec());
        capture.Containment = new Containment.ProcessContainer { CaptureDenials = new CaptureDenialsPolicy() };

        await Assert.ThrowsAsync<SandboxRefusedException>(() => SandboxLauncher.SpawnAsync(learning, MinTier.ProcessIsolation, new ResourceLimits(1 << 30, 4)));
        await Assert.ThrowsAsync<SandboxRefusedException>(() => SandboxLauncher.SpawnAsync(capture, MinTier.AppContainer, new ResourceLimits(1 << 30, 4)));
    }
}
