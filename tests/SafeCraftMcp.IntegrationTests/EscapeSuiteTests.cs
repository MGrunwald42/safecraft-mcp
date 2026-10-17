using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace SafeCraftMcp.IntegrationTests;

/// <summary>Builds tests/HostileWorkload (Rust, std only), once for every class in <see cref="HostileWorkloadCollection"/>.</summary>
public sealed class HostileWorkloadBuild : IDisposable
{
    public string Folder { get; } = Directory.CreateTempSubdirectory("safecraft-hostile-").FullName;
    public string Exe => Path.Combine(Folder, "release", "hostile-workload.exe");
    public string? Error { get; }

    public HostileWorkloadBuild()
    {
        if (MxcHost.SkipReason is not null)
            return;
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "SafeCraftMcp.slnx")))
            repo = repo.Parent;
        var manifest = Path.Combine(repo!.FullName, "tests", "HostileWorkload", "Cargo.toml");
        var cargo = new ProcessStartInfo("cargo", ["build", "--release", "--manifest-path", manifest, "--target-dir", Folder])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(cargo)!;
            _ = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(Exe))
                Error = $"Building HostileWorkload failed:\n{errors}";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Error = $"The escape suite needs cargo (the Rust toolchain) to build its workload: {ex.Message}";
        }
    }

    public void Dispose() => Directory.Delete(Folder, recursive: true);
}

/// <summary>
/// The classes that run HostileWorkload: the escape suite and its fake-MCP mode. A collection, not
/// a class fixture each, so it's built once (a class fixture would build it again per class).
/// </summary>
[CollectionDefinition(Name)]
public sealed class HostileWorkloadCollection : ICollectionFixture<HostileWorkloadBuild>
{
    public const string Name = "HostileWorkload";
}

// The escape suite: HostileWorkload runs in exactly the sandbox a
// session gets (SessionManager.SpecFor) and tries every way out. The protected targets are made in
// a fake user profile in temp, so every attempt always runs, on any host; the real profile's
// targets are tried too where they exist. Each attempt must fail for the right reason (access
// denied, or the socket refusing), not just fail. Then the gateway's export must refuse the links
// the workload planted in its own workspace.
[Collection(HostileWorkloadCollection.Name)]
[Trait("Category", "Mxc")]
public sealed class EscapeSuiteTests(HostileWorkloadBuild build, ITestOutputHelper output) : IAsyncDisposable
{
    private const string Secret = "SAFECRAFT_ESCAPE_SECRET";
    private static readonly TimeSpan WorkloadTimeout = TimeSpan.FromMinutes(1);

    private readonly string _root = RealPath.ResolveFolder(Directory.CreateTempSubdirectory("safecraft-escape-").FullName, out _)!;

    public ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Secret, null);
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
            Directory.Delete(link);
        DeleteFolder(_root);
        return ValueTask.CompletedTask;
    }

    private static string Made(string path, string content = "sentinel")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [MxcFact]
    public async Task Every_Way_Out_Fails()
    {
        Assert.Null(build.Error);
        // A fake user profile with everything the threat model protects.
        var home = Path.Combine(_root, "home");
        var host = new HostPaths(home, Path.Combine(home, "AppData", "Roaming"), Path.Combine(_root, "local"), Path.Combine(home, "Documents"), Directory.CreateDirectory(Path.Combine(_root, "temp")).FullName);
        var repos = Path.Combine(home, "Documents", "GitHub");
        var sshKey = Made(Path.Combine(home, ".ssh", "secret.png"), "PRIVATE KEY");
        var claudeJson = Made(Path.Combine(home, ".claude.json"), "{}");
        var desktopConfig = Made(Path.Combine(host.AppData, "Claude", "claude_desktop_config.json"), "{}");
        var mcpJson = Made(Path.Combine(repos, "somecraft", ".mcp.json"), "{}");
        var repoFile = Made(Path.Combine(repos, "somecraft", "README.md"));
        var gitconfig = Made(Path.Combine(home, ".gitconfig"));
        var settings = GatewaySettings.Defaults(host) with { Repos = repos };
        await using var sessions = new SessionManager(settings, AppRegistry.LoadEmbedded(), new BinaryResolver(settings), host, NullLoggerFactory.Instance);
        var other = SessionPaths.Create(host.SessionsDir, "other");
        var theirs = Made(Path.Combine(other.Work, "theirs.png"));
        var paths = SessionPaths.Create(host.SessionsDir, "hostile");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Environment.SetEnvironmentVariable(Secret, "host-only");

        string[] mandatory =
        [
            $"read-dir:{Path.GetDirectoryName(sshKey)}", $"read-file:{sshKey}", $"read-dir:{repos}", $"read-dir:{home}",
            $"read-dir:{other.Work}", $"read-file:{theirs}", $"read-file:{repoFile}", $"read-file:{gitconfig}", $"read-file:{claudeJson}",
            $"write-open:{claudeJson}", $"write-open:{desktopConfig}", $"write-open:{mcpJson}", $"write-open:{repoFile}",
            $"create-file:{paths.Root}", $"create-file:{host.SessionsDir}", $"create-file:{host.StateDir}",
            $"create-file:{other.Work}", $"create-file:{home}", $"create-file:{repos}",
            "tcp:1.1.1.1:443", $"tcp:127.0.0.1:{port}", "clipboard", $"env:{Secret}",
            $"spawn-read:{sshKey}", $"spawn-read:{claudeJson}",
            $"expect-write:{paths.Work}", $"expect-write:{paths.Profile}",
            $"plant-junction:{Path.Combine(paths.Work, "keys")}|{Path.GetDirectoryName(sshKey)}",
            $"plant-hardlink:{Path.Combine(paths.Work, "innocent.png")}|{sshKey}",
        ];
        // The real profile's own targets, where they exist on this host.
        var real = HostPaths.Current();
        var optional = new[]
        {
            $"read-dir:{Path.Combine(real.UserProfile, ".ssh")}", $"read-dir:{real.DefaultRepos}", $"read-dir:{real.UserProfile}",
            $"read-file:{Path.Combine(real.UserProfile, ".gitconfig")}", $"write-open:{Path.Combine(real.UserProfile, ".claude.json")}",
            $"write-open:{Path.Combine(real.AppData, "Claude", "claude_desktop_config.json")}", $"create-file:{Path.GetTempPath()}",
        }.Where(a => Path.Exists(a[(a.IndexOf(':') + 1)..])).ToList();

        var workload = new AppManifest
        {
            Id = "hostile", Display = "HostileWorkload", Repo = "-", Binary = "hostile-workload.exe", Search = [], Args = [],
            Ready = true, CallTimeoutSeconds = 60, Import = [], Export = [], MaxFileMb = 1,
        };
        var frozen = BinaryFreezer.Freeze(build.Exe, workload, host.StateDir, pin: null);
        // The binary's own folder is mounted read-only: no swapping the exe from inside.
        string[] binary = [$"create-file:{Path.GetDirectoryName(frozen.Path)}", $"write-open:{frozen.Path}"];
        var attempts = mandatory.Concat(binary).Concat(optional).ToList();
        var request = SandboxRequest.Build(sessions.SpecFor(workload with { Args = attempts }, frozen.Path, paths, "hostile"));
        var (process, tier, job) = await SandboxLauncher.SpawnAsync(request, settings.MinTier, sessions.Limits);
        string stdout;
        using (job)
        using (process)
        {
            var read = new StreamReader(process.StandardOutput!).ReadToEndAsync();
            var errors = new StreamReader(process.StandardError!).ReadToEndAsync();
            var exit = await process.WaitAsync().WaitAsync(WorkloadTimeout);
            stdout = await read;
            output.WriteLine($"workload exit {exit.ExitCode:X8}, stderr: {await errors}");
        }

        var results = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToDictionary(r => r.GetProperty("attempt").GetString()!, r => (Through: r.GetProperty("escaped").GetBoolean(), Detail: r.GetProperty("detail").GetString()!));
        var wrong = new List<string>();
        foreach (var attempt in attempts)
        {
            if (!results.TryGetValue(attempt, out var result))
            {
                wrong.Add($"{attempt}: never ran");
                continue;
            }
            var colon = attempt.IndexOf(':');
            var kind = colon > 0 ? attempt[..colon] : attempt;
            var (verdict, ok) = kind switch
            {
                "expect-write" => (result.Through ? "worked" : "FAILED", result.Through),
                "plant-junction" or "plant-hardlink" => (result.Through ? "planted" : "not planted", true),
                // Access denied (os error 5), or the socket refused (10013): the sandbox said no.
                "read-dir" or "read-file" or "write-open" or "create-file" =>
                    (result.Through ? "ESCAPED" : "held", !result.Through && result.Detail.Contains("os error 5")),
                "tcp" => (result.Through ? "ESCAPED" : "held", !result.Through && result.Detail.Contains("os error 10013")),
                "clipboard" => (result.Through ? "ESCAPED" : "held", !result.Through && result.Detail.Contains("user32.dll doesn't load")),
                _ => (result.Through ? "ESCAPED" : "held", !result.Through),
            };
            output.WriteLine($"{verdict,-11} {attempt} — {result.Detail}");
            if (!ok)
                wrong.Add($"{attempt}: {verdict}, {result.Detail}");
        }
        Assert.Equal(Microsoft.Mxc.Sdk.V1.IsolationTier.BaseContainer, tier);
        Assert.True(wrong.Count == 0, "Wrong outcomes:\n" + string.Join("\n", wrong));
        Assert.False(listener.Pending());

        // Whatever the workload planted, the gateway's export must not follow it out of the workspace.
        var outbox = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        var workReal = RealPath.ResolveFolder(paths.Work, out _)!;
        var rules = new FileRules(["png"], 1024 * 1024, ProtectedPaths: []);
        foreach (var planted in new[] { "keys/secret.png", "innocent.png" })
        {
            if (Path.Exists(Path.Combine(paths.Work, planted)))
                Assert.Throws<FileTransferException>(() => FileTransfer.Copy(workReal, planted, outbox, null, rules));
        }
        Assert.Empty(Directory.EnumerateFiles(outbox));
    }
}
