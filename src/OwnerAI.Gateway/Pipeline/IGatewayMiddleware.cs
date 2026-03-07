using OwnerAI.Shared;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 消息处理委托
/// </summary>
public delegate ValueTask MessageDelegate(MessageContext context, CancellationToken ct);

/// <summary>
/// 中间件接口 — 类似 ASP.NET Core 管道但面向消息处理
/// </summary>
public interface IGatewayMiddleware
{
    ValueTask InvokeAsync(MessageContext context, MessageDelegate next, CancellationToken ct);
}
