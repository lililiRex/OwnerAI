using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 限流中间件 — 基于滑动窗口的简易限流
/// </summary>
public sealed class RateLimitMiddleware(ILogger<RateLimitMiddleware> logger) : IGatewayMiddleware
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows = new();
    private const int MaxRequestsPerMinute = 60;

    public ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct)
    {
        var key = context.SenderId ?? context.ChannelId;
        var now = DateTimeOffset.Now;

        var queue = _windows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            // 清理一分钟前的请求
            while (queue.Count > 0 && queue.Peek() < now.AddMinutes(-1))
                queue.Dequeue();

            if (queue.Count >= MaxRequestsPerMinute)
            {
                logger.LogWarning("[RateLimit] Rate limit exceeded for {Key}", key);
                context.Response = new Shared.ReplyPayload
                {
                    Text = "请求过于频繁，请稍后再试。"
                };
                return ValueTask.CompletedTask;
            }

            queue.Enqueue(now);
        }

        return next(context, ct);
    }
}
