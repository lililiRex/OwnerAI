using Microsoft.Extensions.Logging;
using OwnerAI.Gateway.Pipeline;
using OwnerAI.Gateway.Sessions;
using OwnerAI.Shared;
using OwnerAI.Shared.Abstractions;
using OwnerAI.Shared.Events;

namespace OwnerAI.Gateway;

/// <summary>
/// Gateway 引擎主类 — 生命周期管理，消息入口
/// </summary>
public sealed class GatewayEngine
{
    private readonly MessageDelegate _pipeline;
    private readonly ISessionManager _sessionManager;
    private readonly IEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly ILogger<GatewayEngine> _logger;

    public GatewayEngine(
        GatewayPipeline pipeline,
        ISessionManager sessionManager,
        IEventBus eventBus,
        IServiceProvider services,
        ILogger<GatewayEngine> logger)
    {
        _pipeline = pipeline.Build();
        _sessionManager = sessionManager;
        _eventBus = eventBus;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// 处理入站消息 — 消息入口
    /// </summary>
    /// <param name="message">入站消息</param>
    /// <param name="onStreamChunk">流式输出回调 — 每产生一个文本片段就调用</param>
    /// <param name="onModelEvent">模型交互事件回调 — 主模型调度次级模型时实时通知</param>
    /// <param name="onToolCall">工具调用回调 — 实时通知正在执行的工具</param>
    /// <param name="ct">取消令牌</param>
    public async Task<ReplyPayload?> ProcessMessageAsync(
        InboundMessage message,
        Action<string>? onStreamChunk = null,
        Action<ModelInteraction>? onModelEvent = null,
        Action<ToolCallInfo>? onToolCall = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[GatewayEngine] Processing message {MessageId} from {Channel}",
            message.Id, message.ChannelId);

        // 1. 获取/创建会话
        var session = await _sessionManager.GetOrCreateSessionAsync(
            message.ChannelId, message.SenderId, ct);

        // 2. 构建消息上下文
        var context = new MessageContext
        {
            SessionId = session.Id,
            ChannelId = message.ChannelId,
            Message = message,
            SenderId = message.SenderId,
            CancellationToken = ct,
            Services = _services,
            OnStreamChunk = onStreamChunk,
            OnModelEvent = onModelEvent,
            OnToolCall = onToolCall,
        };

        // 3. 发布消息接收事件
        await _eventBus.PublishAsync(
            new MessageReceivedEvent(message.ChannelId, message.SenderId, message), ct);

        // 4. 执行管道
        await _pipeline(context, ct);

        // 5. 增加对话轮次
        await _sessionManager.IncrementTurnAsync(session.Id, ct);

        // 6. 发布回复事件
        if (context.Response is not null)
        {
            await _eventBus.PublishAsync(
                new AgentReplyEvent(session.Id, context.Response), ct);
        }

        return context.Response;
    }
}
