using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Gateway.Events;

/// <summary>
/// 基于 System.Threading.Channels 的内存事件总线
/// </summary>
public sealed class InMemoryEventBus(ILogger<InMemoryEventBus> logger) : IEventBus
{
    private readonly ConcurrentDictionary<Type, object> _channels = new();
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();

    public async ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IOwnerAIEvent
    {
        logger.LogDebug("[EventBus] Publishing {EventType}: {EventId}",
            typeof(TEvent).Name, @event.EventId);

        // 写入 Channel 供 SubscribeAsync 消费
        var channel = GetOrCreateChannel<TEvent>();
        await channel.Writer.WriteAsync(@event, ct);

        // 同步调用已注册的 handler
        if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            foreach (var handler in handlers.ToArray())
            {
                try
                {
                    var typedHandler = (Func<TEvent, CancellationToken, ValueTask>)handler;
                    await typedHandler(@event, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[EventBus] Handler failed for {EventType}", typeof(TEvent).Name);
                }
            }
        }
    }

    public async IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        [EnumeratorCancellation] CancellationToken ct)
        where TEvent : IOwnerAIEvent
    {
        var channel = GetOrCreateChannel<TEvent>();
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IOwnerAIEvent
    {
        var handlers = _handlers.GetOrAdd(typeof(TEvent), _ => []);
        lock (handlers)
        {
            handlers.Add(handler);
        }

        return new Unsubscriber(() =>
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        });
    }

    private Channel<TEvent> GetOrCreateChannel<TEvent>() where TEvent : IOwnerAIEvent
    {
        return (Channel<TEvent>)_channels.GetOrAdd(
            typeof(TEvent),
            _ => Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
            }));
    }

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
