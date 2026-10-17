using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SafeCraftMcp.Server;

/// <summary>
/// Forwards an app's progress notifications to the gateway's client under the client's own
/// progress token. Only the numbers pass: the message is app text, which would reach the agent
/// unlabeled (local rule L6). Fire-and-forget on purpose: <see cref="IProgress{T}.Report"/> can't
/// wait, and a lost progress update must never fail or slow the call it reports on.
/// </summary>
public sealed class ClientProgress(McpServer server, ProgressToken token) : IProgress<ProgressNotificationValue>
{
    public void Report(ProgressNotificationValue value) =>
        _ = server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = value.Progress, Total = value.Total });
}
