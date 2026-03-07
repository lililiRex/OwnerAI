using OwnerAI.Shared;

namespace OwnerAI.Gateway.Pipeline;

/// <summary>
/// 消息上下文 — 贯穿整个管道的上下文对象
/// </summary>
public sealed class MessageContext
{
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required InboundMessage Message { get; init; }
    public string? SenderId { get; init; }
    public bool IsOwner { get; set; }
    public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();
    public ReplyPayload? Response { get; set; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// 服务提供者 — 中间件运行时注入
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// 流式输出回调 — 每产生一个文本片段就调用，用于实时推送到 UI
    /// </summary>
    public Action<string>? OnStreamChunk { get; set; }

    /// <summary>
    /// 模型交互事件回调 — 主模型调度次级模型时实时通知 UI
    /// </summary>
    public Action<ModelInteraction>? OnModelEvent { get; set; }

    /// <summary>
    /// 工具调用回调 — 通知 UI 工具正在执行
    /// </summary>
    public Action<ToolCallInfo>? OnToolCall { get; set; }
}
