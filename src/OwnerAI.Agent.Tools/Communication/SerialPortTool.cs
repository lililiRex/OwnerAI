using System.IO.Ports;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Communication;

/// <summary>
/// 串口通信工具 — 让 AI 能通过 RS-232/RS-485 串口与硬件设备通信
/// 支持 list_ports/open/send/receive/close 操作
/// </summary>
[Tool("serial_communicate", "通过串口(RS-232/RS-485)与硬件设备通信（列出端口、打开、发送、接收、关闭）",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 30)]
public sealed class SerialPortTool : IOwnerAITool, IDisposable
{
    private SerialPort? _port;

    public bool IsAvailable(ToolContext context) => OperatingSystem.IsWindows();

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("action", out var actionEl))
            return ToolResult.Error("缺少参数: action (list_ports/open/send/receive/close)");

        var action = actionEl.GetString()?.ToLowerInvariant();
        return action switch
        {
            "list_ports" => ListPorts(),
            "open" => OpenPort(parameters),
            "send" => await SendAsync(parameters, ct),
            "receive" => await ReceiveAsync(parameters, ct),
            "close" => ClosePort(),
            _ => ToolResult.Error($"未知操作: {action}，可用操作: list_ports/open/send/receive/close"),
        };
    }

    private static ToolResult ListPorts()
    {
        var ports = SerialPort.GetPortNames();
        if (ports.Length == 0)
            return ToolResult.Ok("未检测到可用串口");

        var sb = new StringBuilder("可用串口:\n");
        foreach (var port in ports)
            sb.AppendLine($"  • {port}");

        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult OpenPort(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("port_name", out var nameEl))
            return ToolResult.Error("缺少参数: port_name (如 COM3)");

        var portName = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(portName))
            return ToolResult.Error("port_name 不能为空");

        var baudRate = parameters.TryGetProperty("baud_rate", out var brEl) && brEl.TryGetInt32(out var br) ? br : 9600;
        var dataBits = parameters.TryGetProperty("data_bits", out var dbEl) && dbEl.TryGetInt32(out var db) ? db : 8;

        var parity = Parity.None;
        if (parameters.TryGetProperty("parity", out var parEl))
        {
            parity = parEl.GetString()?.ToLowerInvariant() switch
            {
                "odd" => Parity.Odd,
                "even" => Parity.Even,
                "mark" => Parity.Mark,
                "space" => Parity.Space,
                _ => Parity.None,
            };
        }

        var stopBits = StopBits.One;
        if (parameters.TryGetProperty("stop_bits", out var sbEl))
        {
            stopBits = sbEl.GetString()?.ToLowerInvariant() switch
            {
                "2" or "two" => StopBits.Two,
                "1.5" or "onepointfive" => StopBits.OnePointFive,
                _ => StopBits.One,
            };
        }

        try
        {
            _port?.Dispose();
            _port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000,
                Encoding = GetEncoding(parameters),
            };
            _port.Open();

            return ToolResult.Ok($"已打开串口 {portName} (波特率:{baudRate}, 数据位:{dataBits}, 校验:{parity}, 停止位:{stopBits})");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"打开串口失败: {ex.Message}",
                errorCode: "serial_open_failed",
                failureCategory: ToolFailureCategory.EnvironmentError);
        }
    }

    private async Task<ToolResult> SendAsync(JsonElement parameters, CancellationToken ct)
    {
        if (_port is null || !_port.IsOpen)
            return ToolResult.Error("串口未打开，请先使用 open 操作");

        if (!parameters.TryGetProperty("data", out var dataEl))
            return ToolResult.Error("缺少参数: data");

        var data = dataEl.GetString();
        if (string.IsNullOrEmpty(data))
            return ToolResult.Error("发送数据不能为空");

        try
        {
            // 检查是否为十六进制模式
            if (parameters.TryGetProperty("hex", out var hexEl)
                && hexEl.ValueKind == JsonValueKind.True)
            {
                var hexBytes = Convert.FromHexString(data.Replace(" ", "").Replace("-", ""));
                await _port.BaseStream.WriteAsync(hexBytes, ct);
                return ToolResult.Ok($"已发送 {hexBytes.Length} 字节 (HEX) 到 {_port.PortName}");
            }

            var encoding = GetEncoding(parameters);
            var bytes = encoding.GetBytes(data);
            await _port.BaseStream.WriteAsync(bytes, ct);
            return ToolResult.Ok($"已发送 {bytes.Length} 字节到 {_port.PortName}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"发送失败: {ex.Message}",
                errorCode: "serial_send_failed",
                retryable: true);
        }
    }

    private async Task<ToolResult> ReceiveAsync(JsonElement parameters, CancellationToken ct)
    {
        if (_port is null || !_port.IsOpen)
            return ToolResult.Error("串口未打开，请先使用 open 操作");

        var bufferSize = parameters.TryGetProperty("buffer_size", out var bsEl) && bsEl.TryGetInt32(out var bs) ? bs : 1024;
        var timeoutMs = parameters.TryGetProperty("timeout_ms", out var toEl) && toEl.TryGetInt32(out var to) ? to : 5000;

        try
        {
            var buffer = new byte[bufferSize];
            _port.ReadTimeout = timeoutMs;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs + 1000);

            var bytesRead = await _port.BaseStream.ReadAsync(buffer.AsMemory(0, bufferSize), cts.Token);

            if (bytesRead == 0)
                return ToolResult.Ok("无可用数据");

            var hexOutput = parameters.TryGetProperty("hex", out var hexEl)
                && hexEl.ValueKind == JsonValueKind.True;

            if (hexOutput)
            {
                var hex = Convert.ToHexString(buffer.AsSpan(0, bytesRead));
                return ToolResult.Ok($"收到 {bytesRead} 字节 (HEX):\n{hex}");
            }

            var encoding = GetEncoding(parameters);
            var text = encoding.GetString(buffer, 0, bytesRead);
            return ToolResult.Ok($"收到 {bytesRead} 字节:\n{text}");
        }
        catch (TimeoutException)
        {
            return ToolResult.Ok($"接收超时 ({timeoutMs}ms)，无可用数据");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Ok($"接收超时 ({timeoutMs}ms)，无可用数据");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"接收失败: {ex.Message}",
                errorCode: "serial_receive_failed",
                retryable: true);
        }
    }

    private ToolResult ClosePort()
    {
        if (_port is null || !_port.IsOpen)
            return ToolResult.Ok("无打开的串口");

        var name = _port.PortName;
        _port.Close();
        _port.Dispose();
        _port = null;
        return ToolResult.Ok($"已关闭串口 {name}");
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
        _port?.Dispose();
        _port = null;
    }
}
