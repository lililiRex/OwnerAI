using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;
using OwnerAI.Shared;

namespace OwnerAI.Gateway.Channels;

/// <summary>
/// TCP 服务端渠道适配器 — 监听 TCP 端口，每条入站连接的数据作为消息
/// </summary>
public sealed class TcpChannelAdapter : IChannelAdapter
{
    private readonly int _port;
    private readonly ILogger<TcpChannelAdapter> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();

    public string ChannelId { get; }
    public string DisplayName { get; }
    public ChannelCapabilities Capabilities => ChannelCapabilities.Text;
    public ChannelHealth Health { get; private set; } = ChannelHealth.Unknown;

    public TcpChannelAdapter(string channelId, int port, ILogger<TcpChannelAdapter> logger)
    {
        ChannelId = channelId;
        DisplayName = $"TCP:{port}";
        _port = port;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Health = ChannelHealth.Connected;
        _logger.LogInformation("[TcpChannel] Listening on port {Port}", _port);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<InboundMessage> GetInboundMessagesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_listener is null || _cts is null)
            yield break;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
            _clients[clientId] = client;

            // 读取数据
            var buffer = new byte[4096];
            int bytesRead;
            try
            {
                bytesRead = await client.GetStream().ReadAsync(buffer, linked.Token);
            }
            catch
            {
                _clients.TryRemove(clientId, out _);
                client.Dispose();
                continue;
            }

            if (bytesRead > 0)
            {
                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                yield return new InboundMessage
                {
                    Id = Ulid.NewUlid().ToString(),
                    ChannelId = ChannelId,
                    SenderId = clientId,
                    SenderName = clientId,
                    Text = text,
                };
            }
        }
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (_clients.TryGetValue(message.RecipientId, out var client) && client.Connected)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message.Text);
                await client.GetStream().WriteAsync(bytes, ct);
                return SendResult.Success();
            }
            catch (Exception ex)
            {
                return SendResult.Failed(ex.Message);
            }
        }
        return SendResult.Failed($"Client '{message.RecipientId}' not connected");
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var (_, client) in _clients)
            client.Dispose();
        _clients.Clear();
        Health = ChannelHealth.Disconnected;
        _logger.LogInformation("[TcpChannel] Stopped on port {Port}", _port);
        await Task.CompletedTask;
    }
}
