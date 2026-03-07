using System.Text.Json;
using OwnerAI.Agent.Tools.Shell;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Clipboard;

/// <summary>
/// 剪贴板工具 — 让 AI 能读写系统剪贴板
/// </summary>
[Tool("clipboard", "读取或写入系统剪贴板内容",
    SecurityLevel = ToolSecurityLevel.Low,
    TimeoutSeconds = 5)]
public sealed class ClipboardTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        var action = parameters.TryGetProperty("action", out var actionEl)
            ? actionEl.GetString() ?? "read"
            : "read";

        try
        {
            if (action.Equals("write", StringComparison.OrdinalIgnoreCase))
            {
                if (!parameters.TryGetProperty("content", out var contentEl))
                    return ToolResult.Error("写入模式需要参数: content");

                var content = contentEl.GetString() ?? string.Empty;

                // 使用 PowerShell 设置剪贴板（避免 STA 线程问题）
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ShellDetector.GetPowerShellPath(),
                    Arguments = $"-NoProfile -NonInteractive -Command \"Set-Clipboard -Value '{content.Replace("'", "''")}'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is not null)
                    await process.WaitForExitAsync(ct);

                return ToolResult.Ok($"已写入剪贴板 ({content.Length} 字符)");
            }
            else
            {
                // 读取剪贴板
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ShellDetector.GetPowerShellPath(),
                    Arguments = "-NoProfile -NonInteractive -Command \"Get-Clipboard\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null)
                    return ToolResult.Error("无法读取剪贴板");

                var text = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (string.IsNullOrWhiteSpace(text))
                    return ToolResult.Ok("(剪贴板为空)");

                if (text.Length > 10_000)
                    text = string.Concat(text.AsSpan(0, 10_000), "\n... (已截断)");

                return ToolResult.Ok(text);
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"剪贴板操作失败: {ex.Message}");
        }
    }
}
