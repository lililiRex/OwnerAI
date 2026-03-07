using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;
using OwnerAI.Shared;

namespace OwnerAI.Gateway.Channels;

/// <summary>
/// 串口渠道适配器 — 监听串口数据，每条完整数据帧作为一条消息
/// </summary>
public sealed class SerialChannelAdapter : IChannelAdapter
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly ILogger<SerialChannelAdapter> _logger;
    private SerialPort? _serial;
    private CancellationTokenSource? _cts;
    private readonly Channel<InboundMessage> _inbound = Channel.CreateUnbounded<InboundMessage>();

    public string ChannelId { get; }
    public string DisplayName { get; }
    public ChannelCapabilities Capabilities => ChannelCapabilities.Text;
    public ChannelHealth Health { get; private set; } = ChannelHealth.Unknown;

    public SerialChannelAdapter(
        string channelId, string portName, int baudRate,
        ILogger<SerialChannelAdapter> logger)
    {
        ChannelId = channelId;
        DisplayName = $"Serial:{portName}@{baudRate}";
        _portName = portName;
        _baudRate = baudRate;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _serial = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.UTF8,
            ReadTimeout = 1000,
        };
        _serial.DataReceived += OnDataReceived;
        _serial.Open();
        Health = ChannelHealth.Connected;
        _logger.LogInformation("[SerialChannel] Opened {Port} @ {Baud}", _portName, _baudRate);
        return Task.CompletedTask;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serial is null || !_serial.IsOpen) return;

        try
        {
            var data = _serial.ReadExisting();
            if (!string.IsNullOrEmpty(data))
            {
                var msg = new InboundMessage
                {
                    Id = Ulid.NewUlid().ToString(),
                    ChannelId = ChannelId,
                    SenderId = _portName,
                    SenderName = _portName,
                    Text = data,
                };
                _inbound.Writer.TryWrite(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SerialChannel] Read error on {Port}", _portName);
        }
    }

    public async IAsyncEnumerable<InboundMessage> GetInboundMessagesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_cts is null)
            yield break;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        await foreach (var msg in _inbound.Reader.ReadAllAsync(linked.Token))
        {
            yield return msg;
        }
    }

    public async Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (_serial is null || !_serial.IsOpen)
            return SendResult.Failed("Serial port not open");

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.Text);
            await _serial.BaseStream.WriteAsync(bytes, ct);
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
        _inbound.Writer.Complete();
        if (_serial is not null)
        {
            _serial.DataReceived -= OnDataReceived;
            if (_serial.IsOpen)
                _serial.Close();
            _serial.Dispose();
        }
        Health = ChannelHealth.Disconnected;
        _logger.LogInformation("[SerialChannel] Closed {Port}", _portName);
        await Task.CompletedTask;
    }
}
