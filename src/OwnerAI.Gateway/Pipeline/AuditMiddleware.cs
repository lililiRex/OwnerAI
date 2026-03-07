using Microsoft.Extensions.Logging;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 审计中间件 — 记录所有操作到审计日志
/// </summary>
public sealed class AuditMiddleware(ILogger<AuditMiddleware> logger) : IGatewayMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct)
    {
        await next(context, ct);

        logger.LogInformation(
            "[Audit] Session={Session}, Channel={Channel}, Sender={Sender}, " +
            "HasResponse={HasResponse}, IsOwner={IsOwner}",
            context.SessionId,
            context.ChannelId,
            context.SenderId ?? "unknown",
            context.Response is not null,
            context.IsOwner);
    }
}
