using OwnerAI.Gateway.Pipeline;
using OwnerAI.Shared;

namespace OwnerAI.Gateway.Tests.Pipeline;

public class GatewayPipelineTests
{
    [Fact]
    public async Task Build_NoMiddleware_ReturnsTerminal()
    {
        var pipeline = new GatewayPipeline();
        var handler = pipeline.Build();

        var context = CreateTestContext();
        await handler(context, CancellationToken.None);

        // 无中间件，不会设置 Response
        Assert.Null(context.Response);
    }

    [Fact]
    public async Task Build_SingleMiddleware_Executes()
    {
        var executed = false;
        var pipeline = new GatewayPipeline();
        pipeline.Use((ctx, next, ct) =>
        {
            executed = true;
            return next(ctx, ct);
        });

        var handler = pipeline.Build();
        await handler(CreateTestContext(), CancellationToken.None);

        Assert.True(executed);
    }

    [Fact]
    public async Task Build_MultipleMiddleware_ExecutesInOrder()
    {
        var order = new List<int>();
        var pipeline = new GatewayPipeline();

        pipeline.Use((ctx, next, ct) =>
        {
            order.Add(1);
            return next(ctx, ct);
        });
        pipeline.Use((ctx, next, ct) =>
        {
            order.Add(2);
            return next(ctx, ct);
        });
        pipeline.Use((ctx, next, ct) =>
        {
            order.Add(3);
            return next(ctx, ct);
        });

        var handler = pipeline.Build();
        await handler(CreateTestContext(), CancellationToken.None);

        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task Build_MiddlewareCanShortCircuit()
    {
        var reachedEnd = false;
        var pipeline = new GatewayPipeline();

        pipeline.Use((ctx, next, ct) =>
        {
            ctx.Response = new ReplyPayload { Text = "短路" };
            // 不调用 next
            return ValueTask.CompletedTask;
        });
        pipeline.Use((ctx, next, ct) =>
        {
            reachedEnd = true;
            return next(ctx, ct);
        });

        var handler = pipeline.Build();
        var context = CreateTestContext();
        await handler(context, CancellationToken.None);

        Assert.False(reachedEnd);
        Assert.Equal("短路", context.Response?.Text);
    }

    private static MessageContext CreateTestContext()
    {
        return new MessageContext
        {
            SessionId = "test-session",
            ChannelId = "cli",
            Message = new InboundMessage
            {
                Id = "msg-1",
                ChannelId = "cli",
                SenderId = "owner",
                Text = "hello",
            },
            SenderId = "owner",
            Services = new TestServiceProvider(),
        };
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
