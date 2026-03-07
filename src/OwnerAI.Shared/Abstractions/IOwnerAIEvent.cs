namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 所有事件的基接口
/// </summary>
public interface IOwnerAIEvent
{
    /// <summary>事件 ID</summary>
    string EventId { get; }

    /// <summary>事件发生时间</summary>
    DateTimeOffset Timestamp { get; }
}
