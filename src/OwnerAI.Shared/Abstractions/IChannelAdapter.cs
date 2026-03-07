namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 渠道能力标记
/// </summary>
[Flags]
public enum ChannelCapabilities
{
    None = 0,
    Text = 1 << 0,
    Image = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
    File = 1 << 4,
    Reaction = 1 << 5,
    Thread = 1 << 6,
    RichText = 1 << 7,
    Buttons = 1 << 8,
    Typing = 1 << 9,
}

/// <summary>
/// 渠道健康状态
/// </summary>
public enum ChannelHealth
{
    Unknown,
    Connected,
    Disconnected,
    Error,
}

/// <summary>
/// 渠道适配器接口
/// </summary>
public interface IChannelAdapter : IAsyncDisposable
{
    /// <summary>渠道唯一标识</summary>
    string ChannelId { get; }

    /// <summary>渠道显示名称</summary>
    string DisplayName { get; }

    /// <summary>渠道支持的能力</summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>连接/启动渠道</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>发送出站消息</summary>
    Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct);

    /// <summary>入站消息流</summary>
    IAsyncEnumerable<InboundMessage> GetInboundMessagesAsync(CancellationToken ct);

    /// <summary>健康状态</summary>
    ChannelHealth Health { get; }
}

/// <summary>
/// 发送结果
/// </summary>
public sealed record SendResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public string? MessageId { get; init; }

    public static SendResult Success(string? messageId = null)
        => new() { IsSuccess = true, MessageId = messageId };

    public static SendResult Failed(string error)
        => new() { IsSuccess = false, ErrorMessage = error };
}
