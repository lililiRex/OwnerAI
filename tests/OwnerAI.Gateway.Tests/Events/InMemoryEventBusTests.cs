using Microsoft.Extensions.Logging.Abstractions;
using OwnerAI.Gateway.Events;
using OwnerAI.Shared.Abstractions;
using OwnerAI.Shared.Events;
using OwnerAI.Shared;

namespace OwnerAI.Gateway.Tests.Events;

public class InMemoryEventBusTests
{
    private readonly InMemoryEventBus _sut = new(NullLogger<InMemoryEventBus>.Instance);

    [Fact]
    public async Task Subscribe_ReceivesPublishedEvent()
    {
        var received = false;
        var message = new InboundMessage
        {
            Id = "1",
            ChannelId = "cli",
            SenderId = "owner",
            Text = "hello",
        };

        _sut.Subscribe<MessageReceivedEvent>((e, ct) =>
        {
            received = true;
            Assert.Equal("cli", e.ChannelId);
            return ValueTask.CompletedTask;
        });

        await _sut.PublishAsync(new MessageReceivedEvent("cli", "owner", message));

        Assert.True(received);
    }

    [Fact]
    public async Task Subscribe_MultipleHandlers_AllCalled()
    {
        var count = 0;
        var message = new InboundMessage
        {
            Id = "1",
            ChannelId = "cli",
            SenderId = "owner",
            Text = "hello",
        };

        _sut.Subscribe<MessageReceivedEvent>((_, _) => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
        _sut.Subscribe<MessageReceivedEvent>((_, _) => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });

        await _sut.PublishAsync(new MessageReceivedEvent("cli", "owner", message));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Unsubscribe_StopsReceiving()
    {
        var count = 0;
        var message = new InboundMessage
        {
            Id = "1",
            ChannelId = "cli",
            SenderId = "owner",
            Text = "hello",
        };

        var subscription = _sut.Subscribe<MessageReceivedEvent>((_, _) =>
        {
            Interlocked.Increment(ref count);
            return ValueTask.CompletedTask;
        });

        await _sut.PublishAsync(new MessageReceivedEvent("cli", "owner", message));
        Assert.Equal(1, count);

        subscription.Dispose();

        await _sut.PublishAsync(new MessageReceivedEvent("cli", "owner", message));
        Assert.Equal(1, count);
    }
}
