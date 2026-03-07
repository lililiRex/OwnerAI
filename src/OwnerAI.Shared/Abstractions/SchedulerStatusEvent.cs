namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 调度器状态变更事件 — 用于跨线程通知 UI
/// </summary>
public sealed record SchedulerStatusEvent : IOwnerAIEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>当前调度阶段描述</summary>
    public required string Phase { get; init; }

    /// <summary>是否有任务正在执行</summary>
    public bool IsActive { get; init; }

    /// <summary>当前执行的任务 ID</summary>
    public string? TaskId { get; init; }

    /// <summary>当前执行的任务名称</summary>
    public string? TaskName { get; init; }

    /// <summary>调度器统计</summary>
    public SchedulerStats? Stats { get; init; }
}
