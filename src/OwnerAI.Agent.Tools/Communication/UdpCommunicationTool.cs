using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Communication;

/// <summary>
/// UDP 通信工具 — 让 AI 能通过 UDP 协议发送和接收数据报
/// 支持 send/receive/broadcast/bind/close 操作
/// </summary>
[Tool("udp_communicate", "通过 UDP 协议发送和接收数据报（发送、接收、广播、绑定监听）",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 30)]
public sealed class UdpCommunicationTool : IOwnerAITool, IDisposable
{
    private UdpClient? _client;
    private int _boundPort;

    public bool IsAvailable(ToolContext context) => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("action", out var actionEl))
            return ToolResult.Error("缺少参数: action (send/receive/broadcast/bind/close)");

        var action = actionEl.GetString()?.ToLowerInvariant();
        return action switch
        {
            "send" => await SendAsync(parameters, ct),
            "receive" => await ReceiveAsync(parameters, ct),
            "broadcast" => await BroadcastAsync(parameters, ct),
            "bind" => Bind(parameters),
            "close" => Close(),
            _ => ToolResult.Error($"未知操作: {action}，可用操作: send/receive/broadcast/bind/close"),
        };
    }

    private static async Task<ToolResult> SendAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("host", out var hostEl))
            return ToolResult.Error("缺少参数: host");
        if (!parameters.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port))
            return ToolResult.Error("缺少参数: port (整数)");
        if (!parameters.TryGetProperty("data", out var dataEl))
            return ToolResult.Error("缺少参数: data");

        var host = hostEl.GetString();
        var data = dataEl.GetString();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(data))
            return ToolResult.Error("host 和 data 不能为空");

        try
        {
            using var client = new UdpClient();
            var encoding = GetEncoding(parameters);
            var bytes = encoding.GetBytes(data);
            var sent = await client.SendAsync(bytes, new IPEndPoint(IPAddress.Parse(host), port), ct);
            return ToolResult.Ok($"已发送 {sent} 字节到 {host}:{port}");
        }
        catch (FormatException)
        {
            // 尝试 DNS 解析
            try
            {
                using var client = new UdpClient();
                var encoding = GetEncoding(parameters);
                var bytes = encoding.GetBytes(data!);
                var sent = await client.SendAsync(bytes, bytes.Length, host, port);
                return ToolResult.Ok($"已发送 {sent} 字节到 {host}:{port}");
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"发送失败: {ex.Message}",
                    errorCode: "udp_send_failed", retryable: true);
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"发送失败: {ex.Message}",
                errorCode: "udp_send_failed", retryable: true);
        }
    }

    private async Task<ToolResult> ReceiveAsync(JsonElement parameters, CancellationToken ct)
    {
        if (_client is null)
            return ToolResult.Error("需要先使用 bind 操作绑定端口");

        var timeoutMs = parameters.TryGetProperty("timeout_ms", out var toEl) && toEl.TryGetInt32(out var to) ? to : 5000;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var result = await _client.ReceiveAsync(cts.Token);
            var encoding = GetEncoding(parameters);
            var text = encoding.GetString(result.Buffer);
            return ToolResult.Ok($"收到 {result.Buffer.Length} 字节 (来自 {result.RemoteEndPoint}):\n{text}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Ok($"接收超时 ({timeoutMs}ms)，无可用数据");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"接收失败: {ex.Message}",
                errorCode: "udp_receive_failed", retryable: true);
        }
    }

    private static async Task<ToolResult> BroadcastAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port))
            return ToolResult.Error("缺少参数: port (整数)");
        if (!parameters.TryGetProperty("data", out var dataEl))
            return ToolResult.Error("缺少参数: data");

        var data = dataEl.GetString();
        if (string.IsNullOrEmpty(data))
            return ToolResult.Error("data 不能为空");

        try
        {
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            var encoding = GetEncoding(parameters);
            var bytes = encoding.GetBytes(data);
            var sent = await client.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, port), ct);
            return ToolResult.Ok($"已广播 {sent} 字节到端口 {port}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"广播失败: {ex.Message}",
                errorCode: "udp_broadcast_failed", retryable: true);
        }
    }

    private ToolResult Bind(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port))
            return ToolResult.Error("缺少参数: port (整数)");

        try
        {
            _client?.Dispose();
            _client = new UdpClient(port);
            _boundPort = port;
            return ToolResult.Ok($"已绑定 UDP 端口 {port}，可以使用 receive 接收数据");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"绑定失败: {ex.Message}",
                errorCode: "udp_bind_failed");
        }
    }

    private ToolResult Close()
    {
        if (_client is null)
            return ToolResult.Ok("无活跃的 UDP 绑定");

        _client.Dispose();
        _client = null;
        var port = _boundPort;
        _boundPort = 0;
        return ToolResult.Ok($"已关闭 UDP 端口 {port}");
    }

    private static Encoding GetEncoding(JsonElement parameters)
    {
        if (parameters.TryGetProperty("encoding", out var encEl))
        {
            var enc = encEl.GetString()?.ToLowerInvariant();
            return enc switch
            {
                "ascii" => Encoding.ASCII,
                "utf-16" or "unicode" => Encoding.Unicode,
                "gbk" or "gb2312" => Encoding.GetEncoding("GBK"),
                _ => Encoding.UTF8,
            };
        }
        return Encoding.UTF8;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
