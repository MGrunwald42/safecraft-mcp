using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SafeCraftMcp.Server;

/// <summary>
/// Writes the gateway's log as JSON lines to <c>%LOCALAPPDATA%\safecraft-mcp\logs\</c>, one file per
/// gateway process (several gateways run at once: Desktop chat and Claude Code), so a session's
/// story can be read after the client closed. Information and up only: the apps' stderr is logged
/// at Debug and stays out of the file. Files older than two weeks are deleted at start.
/// </summary>
public sealed class FileLog : ILoggerProvider
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(14);

    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public FileLog(string logsDir)
    {
        Directory.CreateDirectory(logsDir);
        foreach (var old in Directory.EnumerateFiles(logsDir, "safecraft-mcp-*.jsonl"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > Keep)
                    File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another gateway's open file; it's tried again next start.
            }
        }
        Path = System.IO.Path.Combine(logsDir, $"safecraft-mcp-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        _writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Path { get; }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
            _writer.Dispose();
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("time", DateTime.UtcNow.ToString("O"));
            json.WriteString("level", level.ToString());
            json.WriteString("category", category);
            json.WriteString("message", message);
            if (exception is not null)
                json.WriteString("exception", exception.ToString());
            json.WriteEndObject();
        }
        var line = Encoding.UTF8.GetString(buffer.ToArray());
        lock (_lock)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Logging must never take the gateway down (a full disk, shutdown).
            }
        }
    }

    private sealed class Logger(FileLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                log.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
