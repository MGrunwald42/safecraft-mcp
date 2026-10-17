using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;

namespace SafeCraftMcp.Tests;

// A fake user profile in temp with a ~/.claude.json and a Claude Desktop config.
public sealed class ClientConfigsTests : IDisposable
{
    private readonly string _profile = Directory.CreateTempSubdirectory("safecraft-profile-").FullName;
    private readonly HostPaths _host;

    public ClientConfigsTests()
    {
        _host = new HostPaths(_profile, Path.Combine(_profile, "Roaming"), Path.Combine(_profile, "Local"));
        Directory.CreateDirectory(Path.Combine(_host.AppData, "Claude"));
    }

    public void Dispose() => Directory.Delete(_profile, recursive: true);

    private void ClaudeJson(string json) => File.WriteAllText(Path.Combine(_profile, ".claude.json"), json);

    private void DesktopJson(string json) => File.WriteAllText(Path.Combine(_host.AppData, "Claude", "claude_desktop_config.json"), json);

    [Fact]
    public void Registrations_Are_Found_In_User_And_Local_Scope_And_In_Desktop()
    {
        ClaudeJson("""
            {
              "mcpServers": { "safecraft": { "command": "C:\\Tools\\safecraft-mcp.exe", "args": ["--apps", "photocraft"] },
                              "other": { "command": "x.exe" } },
              "projects": { "C:/repo": { "mcpServers": { "art": { "command": "C:\\Tools\\safecraft-mcp.exe", "args": [] } } } }
            }
            """);
        DesktopJson("""{ "mcpServers": { "safecraft": { "command": "C:\\Tools\\safecraft-mcp.exe", "args": ["--apps", "photocraft"] } } }""");

        var scan = ClientConfigs.Find(_host);

        Assert.Equal(["Claude Code/user", "Claude Code/local: C:/repo", "Claude Desktop/user"], scan.Registrations.Select(r => $"{r.Client}/{r.Scope}"));
        Assert.Equal(["--apps", "photocraft"], scan.Registrations[0].Args);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void A_Malformed_Config_Is_Reported_Not_Thrown()
    {
        ClaudeJson("{ not json");
        DesktopJson("""{ "mcpServers": "a string where an object belongs" }""");

        var scan = ClientConfigs.Find(_host);

        Assert.Single(scan.Problems);
        Assert.Empty(scan.Registrations);
    }

    [Fact]
    public void Unexpected_Shapes_Are_Skipped()
    {
        ClaudeJson("""{ "mcpServers": { "safecraft": "x" }, "projects": { "C:/repo": "x" } }""");

        var scan = ClientConfigs.Find(_host);

        Assert.Equal("safecraft", Assert.Single(scan.Registrations).Name);
        Assert.Null(scan.Registrations[0].Command);
    }
}
