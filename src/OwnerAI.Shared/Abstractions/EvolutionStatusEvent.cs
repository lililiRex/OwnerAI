namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 自我进化状态变更事件 — 用于跨线程通知 UI
/// </summary>
public sealed record EvolutionStatusEvent : IOwnerAIEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>当前进化阶段描述</summary>
    public required string Phase { get; init; }

    /// <summary>是否正在进化中</summary>
    public bool IsActive { get; init; }

    /// <summary>关联的缺口 ID</summary>
    public string? GapId { get; init; }

    /// <summary>关联的缺口描述</summary>
    public string? GapDescription { get; init; }

    /// <summary>进化统计 (可选)</summary>
    public EvolutionStats? Stats { get; init; }
}
