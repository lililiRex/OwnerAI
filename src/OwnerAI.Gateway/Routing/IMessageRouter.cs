using OwnerAI.Gateway.Pipeline;

namespace OwnerAI.Gateway.Routing;

/// <summary>
/// 路由结果
/// </summary>
public sealed record RouteResult
{
    public bool Success { get; init; }
    public string? AgentId { get; init; }
    public string? Reason { get; init; }

    public static RouteResult Ok(string agentId)
        => new() { Success = true, AgentId = agentId };

    public static RouteResult Failed(string reason)
        => new() { Success = false, Reason = reason };
}

/// <summary>
/// 消息路由接口
/// </summary>
public interface IMessageRouter
{
    Task<RouteResult> RouteAsync(MessageContext context, CancellationToken ct);
}
