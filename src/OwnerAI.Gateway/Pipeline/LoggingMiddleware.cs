using Microsoft.Extensions.Logging;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 日志中间件 — 记录请求进出
/// </summary>
public sealed class LoggingMiddleware(ILogger<LoggingMiddleware> logger) : IGatewayMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "[Gateway] Incoming message from {Channel}/{Sender}: {Text}",
            context.ChannelId,
            context.SenderId ?? "unknown",
            context.Message.Text.Length > 100
                ? string.Concat(context.Message.Text.AsSpan(0, 100), "...")
                : context.Message.Text);

        try
        {
            await next(context, ct);
            sw.Stop();
            logger.LogInformation(
                "[Gateway] Message processed in {Elapsed}ms, hasResponse={HasResponse}",
                sw.ElapsedMilliseconds,
                context.Response is not null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex,
                "[Gateway] Message processing failed after {Elapsed}ms",
                sw.ElapsedMilliseconds);
            throw;
        }
    }
}
