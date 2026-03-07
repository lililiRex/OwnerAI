namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 事件总线接口 — 基于 System.Threading.Channels
/// </summary>
public interface IEventBus
{
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IOwnerAIEvent;

    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct = default)
        where TEvent : IOwnerAIEvent;

    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IOwnerAIEvent;
}
