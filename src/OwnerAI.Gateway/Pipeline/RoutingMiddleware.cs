using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OwnerAI.Gateway.Routing;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 路由中间件 — 将消息路由到正确的 Agent
/// </summary>
public sealed class RoutingMiddleware(ILogger<RoutingMiddleware> logger) : IGatewayMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct)
    {
        var router = context.Services.GetRequiredService<IMessageRouter>();
        var routeResult = await router.RouteAsync(context, ct);

        if (!routeResult.Success)
        {
            logger.LogWarning("[Routing] Route failed: {Reason}", routeResult.Reason);
            context.Response = new Shared.ReplyPayload
            {
                Text = $"路由失败: {routeResult.Reason}"
            };
            return;
        }

        context.Properties["AgentId"] = routeResult.AgentId!;
        logger.LogDebug("[Routing] Routed to agent {AgentId}", routeResult.AgentId);

        await next(context, ct);
    }
}
