using Microsoft.Extensions.DependencyInjection;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 管道构建器 — 组装中间件链
/// </summary>
public sealed class GatewayPipeline
{
    private readonly List<Func<MessageDelegate, MessageDelegate>> _middlewares = [];

    /// <summary>
    /// 注册中间件类型
    /// </summary>
    public GatewayPipeline Use<TMiddleware>() where TMiddleware : IGatewayMiddleware
    {
        _middlewares.Add(next => (ctx, ct) =>
        {
            var middleware = ctx.Services.GetRequiredService<TMiddleware>();
            return middleware.InvokeAsync(ctx, next, ct);
        });
        return this;
    }

    /// <summary>
    /// 注册中间件实例委托
    /// </summary>
    public GatewayPipeline Use(Func<MessageContext, MessageDelegate, CancellationToken, ValueTask> middleware)
    {
        _middlewares.Add(next => (ctx, ct) => middleware(ctx, next, ct));
        return this;
    }

    /// <summary>
    /// 构建管道 — 从末尾向头部包裹
    /// </summary>
    public MessageDelegate Build()
    {
        MessageDelegate terminal = static (_, _) => ValueTask.CompletedTask;

        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            terminal = _middlewares[i](terminal);
        }

        return terminal;
    }
}
