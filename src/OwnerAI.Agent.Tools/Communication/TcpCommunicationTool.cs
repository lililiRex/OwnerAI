using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Communication;

/// <summary>
/// TCP 通信工具 — 让 AI 能通过 TCP/IP 协议与远程设备/服务通信
/// 支持 connect/send/receive/disconnect/list_connections 操作
/// </summary>
[Tool("tcp_communicate", "通过 TCP/IP 连接远程设备或服务进行数据通信（连接、发送、接收、断开）",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 30)]
public sealed class TcpCommunicationTool : IOwnerAITool, IDisposable
{
    private readonly ConcurrentDictionary<string, TcpClient> _connections = new();

    public bool IsAvailable(ToolContext context) => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("action", out var actionEl))
            return ToolResult.Error("缺少参数: action (connect/send/receive/disconnect/list_connections)");

        var action = actionEl.GetString()?.ToLowerInvariant();
        return action switch
        {
            "connect" => await ConnectAsync(parameters, ct),
            "send" => await SendAsync(parameters, ct),
            "receive" => await ReceiveAsync(parameters, ct),
            "disconnect" => Disconnect(parameters),
            "list_connections" => ListConnections(),
            _ => ToolResult.Error($"未知操作: {action}，可用操作: connect/send/receive/disconnect/list_connections"),
        };
    }

    private async Task<ToolResult> ConnectAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("host", out var hostEl))
            return ToolResult.Error("缺少参数: host");
        if (!parameters.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port))
            return ToolResult.Error("缺少参数: port (整数)");

        var host = hostEl.GetString();
        if (string.IsNullOrWhiteSpace(host))
            return ToolResult.Error("host 不能为空");

        var connId = $"{host}:{port}";
        if (_connections.ContainsKey(connId))
            return ToolResult.Ok($"连接 '{connId}' 已存在");

        try
        {
            var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, cts.Token);
            _connections[connId] = client;
            return ToolResult.Ok($"已成功连接到 {connId}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"连接失败: {ex.Message}",
                errorCode: "tcp_connect_failed",
                retryable: true,
                failureCategory: ToolFailureCategory.RetryableError);
        }
    }

    private async Task<ToolResult> SendAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("connection_id", out var idEl))
            return ToolResult.Error("缺少参数: connection_id (格式: host:port)");
        if (!parameters.TryGetProperty("data", out var dataEl))
            return ToolResult.Error("缺少参数: data");

        var connId = idEl.GetString();
        var data = dataEl.GetString();
        if (string.IsNullOrEmpty(connId) || !_connections.TryGetValue(connId, out var client))
            return ToolResult.Error($"连接 '{connId}' 不存在");

        if (string.IsNullOrEmpty(data))
            return ToolResult.Error("发送数据不能为空");

        try
        {
            var encoding = GetEncoding(parameters);
            var bytes = encoding.GetBytes(data);
            await client.GetStream().WriteAsync(bytes, ct);
            return ToolResult.Ok($"已发送 {bytes.Length} 字节到 {connId}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"发送失败: {ex.Message}",
                errorCode: "tcp_send_failed",
                retryable: true);
        }
    }

    private async Task<ToolResult> ReceiveAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("connection_id", out var idEl))
            return ToolResult.Error("缺少参数: connection_id");

        var connId = idEl.GetString();
        if (string.IsNullOrEmpty(connId) || !_connections.TryGetValue(connId, out var client))
            return ToolResult.Error($"连接 '{connId}' 不存在");

        var bufferSize = parameters.TryGetProperty("buffer_size", out var bsEl) && bsEl.TryGetInt32(out var bs) ? bs : 4096;
        var timeoutMs = parameters.TryGetProperty("timeout_ms", out var toEl) && toEl.TryGetInt32(out var to) ? to : 5000;

        try
        {
            var buffer = new byte[bufferSize];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var stream = client.GetStream();
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cts.Token);

            if (bytesRead == 0)
                return ToolResult.Ok("远端已关闭连接，未收到数据");

            var encoding = GetEncoding(parameters);
            var text = encoding.GetString(buffer, 0, bytesRead);
            return ToolResult.Ok($"收到 {bytesRead} 字节:\n{text}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Ok($"接收超时 ({timeoutMs}ms)，无可用数据");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"接收失败: {ex.Message}",
                errorCode: "tcp_receive_failed",
                retryable: true);
        }
    }

    private ToolResult Disconnect(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("connection_id", out var idEl))
            return ToolResult.Error("缺少参数: connection_id");

        var connId = idEl.GetString();
        if (string.IsNullOrEmpty(connId) || !_connections.TryRemove(connId, out var client))
            return ToolResult.Error($"连接 '{connId}' 不存在");

        client.Dispose();
        return ToolResult.Ok($"已断开连接 {connId}");
    }

    private ToolResult ListConnections()
    {
        if (_connections.IsEmpty)
            return ToolResult.Ok("当前无活跃 TCP 连接");

        var sb = new StringBuilder("活跃 TCP 连接:\n");
        foreach (var (id, client) in _connections)
        {
            sb.AppendLine($"  • {id} — 已连接: {client.Connected}");
        }
        return ToolResult.Ok(sb.ToString());
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
        foreach (var (_, client) in _connections)
            client.Dispose();
        _connections.Clear();
    }
}
