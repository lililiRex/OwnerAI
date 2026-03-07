using Microsoft.Extensions.Logging;
using OwnerAI.Gateway.Pipeline;

namespace OwnerAI.Gateway.Routing;

/// <summary>
/// 消息路由实现 — 当前所有消息路由到默认 Agent
/// </summary>
public sealed class MessageRouter(ILogger<MessageRouter> logger) : IMessageRouter
{
    private const string DefaultAgentId = "default";

    public Task<RouteResult> RouteAsync(MessageContext context, CancellationToken ct)
    {
        // Phase 1: 所有消息路由到默认 Agent
        // 后续可根据渠道、会话、用户意图做更复杂的路由
        logger.LogDebug("[Router] Routing {Channel}/{Sender} → agent:{Agent}",
            context.ChannelId, context.SenderId, DefaultAgentId);

        return Task.FromResult(RouteResult.Ok(DefaultAgentId));
    }
}
