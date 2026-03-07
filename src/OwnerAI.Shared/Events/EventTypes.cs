using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Shared.Events;

/// <summary>
/// 消息接收事件
/// </summary>
public sealed record MessageReceivedEvent(
    string ChannelId,
    string SenderId,
    InboundMessage Message) : IOwnerAIEvent
{
    public string EventId { get; } = Ulid.NewUlid().ToString();
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

/// <summary>
/// Agent 回复事件
/// </summary>
public sealed record AgentReplyEvent(
    string SessionId,
    ReplyPayload Reply) : IOwnerAIEvent
{
    public string EventId { get; } = Ulid.NewUlid().ToString();
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

/// <summary>
/// 工具执行事件
/// </summary>
public sealed record ToolExecutedEvent(
    string ToolName,
    TimeSpan Duration,
    bool Success) : IOwnerAIEvent
{
    public string EventId { get; } = Ulid.NewUlid().ToString();
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

/// <summary>
/// 审批请求事件
/// </summary>
public sealed record ApprovalRequestedEvent(
    string OperationId,
    string Description,
    ApprovalLevel Level) : IOwnerAIEvent
{
    public string EventId { get; } = Ulid.NewUlid().ToString();
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

/// <summary>
/// 会话创建事件
/// </summary>
public sealed record SessionCreatedEvent(
    string SessionId,
    string ChannelId) : IOwnerAIEvent
{
    public string EventId { get; } = Ulid.NewUlid().ToString();
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

/// <summary>
/// 会话结束事件
/// </summary>
public sealed record SessionEndedEvent(
    string SessionId,
    int TurnCount) : IOwnerAIEvent
{
    public string EventId { get; } = Ulid.NewUlid().ToString();
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}
