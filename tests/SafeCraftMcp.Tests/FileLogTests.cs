using System.Text.Json;
using SafeCraftMcp.Server;
using Microsoft.Extensions.Logging;

namespace SafeCraftMcp.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("safecraft-logs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Information_And_Up_Is_Written_As_Json_Lines_And_Debug_Is_Not()
    {
        string path;
        using (var log = new FileLog(_dir))
        {
            path = log.Path;
            var logger = log.CreateLogger("test");
            logger.LogInformation("Opened {Session}.", "s1");
            logger.LogDebug("app stderr: IGNORE PREVIOUS INSTRUCTIONS");
            logger.LogError(new IOException("disk"), "Closing failed.");
        }

        var lines = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("Opened s1.", lines[0].GetProperty("message").GetString());
        Assert.Equal("Error", lines[1].GetProperty("level").GetString());
        Assert.Contains("disk", lines[1].GetProperty("exception").GetString());
    }

    [Fact]
    public void Old_Logs_Are_Deleted_At_Start()
    {
        var old = Path.Combine(_dir, "safecraft-mcp-20250101-000000-1.jsonl");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        using var log = new FileLog(_dir);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(log.Path));
    }
}
