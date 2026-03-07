using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent;

/// <summary>
/// Agent 流式输出块
/// </summary>
public sealed record AgentStreamChunk
{
    /// <summary>文本内容片段</summary>
    public string? Text { get; init; }

    /// <summary>工具调用信息 (用于 UI 展示)</summary>
    public Shared.ToolCallInfo? ToolCall { get; init; }

    /// <summary>模型交互信息 — 主模型与次级模型之间的分发/回复</summary>
    public Shared.ModelInteraction? ModelEvent { get; init; }

    /// <summary>工具提取的媒体资源 — 用于 UI 内联展示，不保存到本地</summary>
    public IReadOnlyList<ToolMediaUrl>? MediaUrls { get; init; }

    /// <summary>是否为最后一个块</summary>
    public bool IsComplete { get; init; }

    public AgentStreamChunk(string text) => Text = text;
    public AgentStreamChunk() { }
}

/// <summary>
/// Agent 接口
/// </summary>
public interface IAgent
{
    /// <summary>
    /// 流式执行 Agent 推理
    /// </summary>
    IAsyncEnumerable<AgentStreamChunk> ExecuteAsync(
        AgentContext context,
        CancellationToken ct = default);
}
