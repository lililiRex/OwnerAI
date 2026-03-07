using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Shared;

/// <summary>
/// 归一化入站消息
/// </summary>
public sealed record InboundMessage
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public string? SenderName { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }
    public string? ThreadId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// 出站消息
/// </summary>
public sealed record OutboundMessage
{
    public required string ChannelId { get; init; }
    public required string RecipientId { get; init; }
    public required string Text { get; init; }
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }
    public string? ThreadId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// 媒体附件
/// </summary>
public sealed record MediaAttachment
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long Size { get; init; }
    public string? Url { get; init; }
    public byte[]? Data { get; init; }
}

/// <summary>
/// 回复负载
/// </summary>
public sealed record ReplyPayload
{
    public required string Text { get; init; }
    public bool IsError { get; init; }
    public IReadOnlyList<MediaAttachment>? Attachments { get; init; }
    public IReadOnlyList<ToolCallInfo>? ToolCalls { get; init; }
    public IReadOnlyList<ModelInteraction>? ModelInteractions { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// 工具调用信息 (用于审计和展示)
/// </summary>
public sealed record ToolCallInfo
{
    public required string ToolName { get; init; }
    public string? Parameters { get; init; }
    public string? Result { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public ToolFailureCategory FailureCategory { get; init; }
    public bool Retryable { get; init; }
    public string? SuggestedFix { get; init; }
    public bool Success { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 模型交互记录 — 主模型调度次级模型的完整过程
/// </summary>
public sealed record ModelInteraction
{
    /// <summary>是分发请求还是次级模型回复</summary>
    public required bool IsRequest { get; init; }

    /// <summary>目标模型名称</summary>
    public required string ModelName { get; init; }

    /// <summary>模型类别 (vision, coding, science 等)</summary>
    public required string Category { get; init; }

    /// <summary>分发的任务描述</summary>
    public required string Task { get; init; }

    /// <summary>次级模型的回复内容 (仅 IsRequest=false 时有值)</summary>
    public string? Response { get; init; }
}
