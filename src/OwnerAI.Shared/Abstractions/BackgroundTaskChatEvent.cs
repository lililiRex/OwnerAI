namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 后台任务聊天消息事件 — 用于将后台任务（进化/定时任务）的执行信息推送到聊天窗口
/// </summary>
public sealed record BackgroundTaskChatEvent : IOwnerAIEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>消息来源: "evolution" | "scheduler"</summary>
    public required string Source { get; init; }

    /// <summary>执行阶段: Start | Progress | Completed | Failed</summary>
    public required BackgroundTaskPhase Phase { get; init; }

    /// <summary>显示在聊天窗口的消息文本</summary>
    public required string Message { get; init; }

    /// <summary>关联的任务名称</summary>
    public string? TaskName { get; init; }

    /// <summary>关联的任务 ID</summary>
    public string? TaskId { get; init; }

    /// <summary>关联的进化缺口 ID（用于在聊天窗口中串联规划/执行/验收）</summary>
    public string? GapId { get; init; }
}

/// <summary>
/// 后台任务执行阶段
/// </summary>
public enum BackgroundTaskPhase
{
    /// <summary>开始执行</summary>
    Start,
    /// <summary>执行中（进度更新）</summary>
    Progress,
    /// <summary>执行完成</summary>
    Completed,
    /// <summary>执行失败</summary>
    Failed,
}
