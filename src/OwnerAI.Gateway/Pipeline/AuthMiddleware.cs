using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 认证中间件 — 验证发送者身份，判断是否为 Owner
/// </summary>
public sealed class AuthMiddleware(
    IOptions<OwnerAIConfig> config,
    ILogger<AuthMiddleware> logger) : IGatewayMiddleware
{
    public ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct)
    {
        // CLI / Desktop 本地渠道 → 默认 Owner
        if (context.ChannelId is "cli" or "desktop")
        {
            context.IsOwner = true;
            return next(context, ct);
        }

        // 远程渠道 → 检查白名单
        var allowed = config.Value.Security.AllowedSenders;
        if (allowed.Count > 0 && context.SenderId is not null)
        {
            context.IsOwner = allowed.Contains(context.SenderId);
        }

        if (!context.IsOwner)
        {
            logger.LogWarning(
                "[Auth] Unauthorized sender {Sender} on channel {Channel}",
                context.SenderId, context.ChannelId);
        }

        return next(context, ct);
    }
}
