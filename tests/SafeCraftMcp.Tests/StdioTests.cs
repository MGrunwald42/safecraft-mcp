using System.Diagnostics;
using System.Text.Json;

namespace SafeCraftMcp.Tests;

// Starts the built safecraft-mcp.exe (Debug or Release, whichever this test run uses) and talks to
// it over stdio, as a client does. Checks what only the real process shows: stdout carries nothing
// but JSON-RPC, and a refused command line exits before serving.
public sealed class StdioTests
{
    private static string Exe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SafeCraftMcp.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var exe = Path.Combine(dir.FullName, "src", "SafeCraftMcp.Server", "bin", configuration, "net10.0", "win-x64", "safecraft-mcp.exe");
        Assert.True(File.Exists(exe), $"Build the server first: {exe}");
        return exe;
    }

    private static Process Start(params string[] args)
    {
        // An unrelated working directory, as Claude Desktop uses.
        var info = new ProcessStartInfo(Exe(), args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
        return Process.Start(info)!;
    }

    [Fact]
    public async Task Stdout_Carries_Only_Json_Rpc()
    {
        using var server = Start();
        string[] requests =
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"settings_show","arguments":{"bogus":1}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"settings_show","arguments":{}}}""",
            // Arguments that aren't an object fail in the SDK before the gateway's handler runs.
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"settings_show","arguments":"x"}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"settings_show","arguments":[1]}}""",
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":7}}""",
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":"x"}""",
        ];
        foreach (var request in requests)
            await server.StandardInput.WriteLineAsync(request);
        var stderr = server.StandardError.ReadToEndAsync();

        var responses = new Dictionary<int, JsonElement>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (responses.Count < 8 && await server.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
            if (doc.RootElement.TryGetProperty("id", out var id))
                responses[id.GetInt32()] = doc.RootElement.Clone();
        }
        server.StandardInput.Close();
        await server.WaitForExitAsync(timeout.Token);
        await stderr;

        Assert.Equal(14, responses[2].GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.Equal(-32602, responses[3].GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(responses[4].GetProperty("result").GetProperty("isError").GetBoolean());
        foreach (var id in new[] { 5, 6, 7, 8 })
            Assert.Equal(-32602, responses[id].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--max-sessions", "99")]
    [InlineData("--repos", "relative")]
    public async Task A_Refused_Command_Line_Exits_With_2_And_Says_Why_On_Stderr(params string[] args)
    {
        using var server = Start(args);
        server.StandardInput.Close();

        var stdout = await server.StandardOutput.ReadToEndAsync();
        var stderr = await server.StandardError.ReadToEndAsync();
        await server.WaitForExitAsync();

        Assert.Equal(2, server.ExitCode);
        Assert.Empty(stdout);
        Assert.Contains("didn't start", stderr);
    }
}
