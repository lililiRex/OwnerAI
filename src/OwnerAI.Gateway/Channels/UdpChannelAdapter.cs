using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;
using OwnerAI.Shared;

namespace OwnerAI.Gateway.Channels;

/// <summary>
/// UDP 渠道适配器 — 监听 UDP 端口，每条数据报作为一条消息
/// </summary>
public sealed class UdpChannelAdapter : IChannelAdapter
{
    private readonly int _port;
    private readonly ILogger<UdpChannelAdapter> _logger;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    public string ChannelId { get; }
    public string DisplayName { get; }
    public ChannelCapabilities Capabilities => ChannelCapabilities.Text;
    public ChannelHealth Health { get; private set; } = ChannelHealth.Unknown;

    public UdpChannelAdapter(string channelId, int port, ILogger<UdpChannelAdapter> logger)
    {
        ChannelId = channelId;
        DisplayName = $"UDP:{port}";
        _port = port;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = new UdpClient(_port);
        Health = ChannelHealth.Connected;
        _logger.LogInformation("[UdpChannel] Listening on port {Port}", _port);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<InboundMessage> GetInboundMessagesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_client is null || _cts is null)
            yield break;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client.ReceiveAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[UdpChannel] Receive error");
                continue;
            }

            var text = Encoding.UTF8.GetString(result.Buffer);
            var senderId = result.RemoteEndPoint.ToString();

            yield return new InboundMessage
            {
                Id = Ulid.NewUlid().ToString(),
                ChannelId = ChannelId,
                SenderId = senderId,
                SenderName = senderId,
                Text = text,
            };
        }
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (_client is null)
            return SendResult.Failed("UDP channel not started");

        try
        {
            // RecipientId 格式: "ip:port"
            var parts = message.RecipientId.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[^1], out var port))
                return SendResult.Failed($"Invalid recipient format: {message.RecipientId}");

            var host = string.Join(':', parts[..^1]);
            var bytes = Encoding.UTF8.GetBytes(message.Text);
            var sent = await _client.SendAsync(bytes, bytes.Length, host, port);
            return SendResult.Success();
        }
        catch (Exception ex)
        {
            return SendResult.Failed(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;
        Health = ChannelHealth.Disconnected;
        _logger.LogInformation("[UdpChannel] Stopped on port {Port}", _port);
        await Task.CompletedTask;
    }
}
