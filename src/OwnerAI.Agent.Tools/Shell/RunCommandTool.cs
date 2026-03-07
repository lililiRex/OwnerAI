using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Shell;

/// <summary>
/// PowerShell 命令执行工具 — 让 AI 能执行系统命令（受安全沙箱保护）
/// </summary>
[Tool("run_command", "在 PowerShell 中执行命令，可用于查询系统信息、管理文件、运行脚本等",
    SecurityLevel = ToolSecurityLevel.High,
    RequiresSandbox = true,
    TimeoutSeconds = 60)]
public sealed class RunCommandTool : IOwnerAITool
{
    private static readonly string[] BlockedCommands =
    [
        "format", "diskpart", "bcdedit", "reg delete", "rd /s",
        "rmdir /s", "del /f /s /q C:", "shutdown", "taskkill /f /im",
        "net user", "net localgroup", "powershell -encodedcommand",
        "remove-item -recurse -force C:", "stop-computer", "restart-computer",
    ];

    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("command", out var cmdEl))
            return ToolResult.Error("缺少参数: command");

        var command = cmdEl.GetString();
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("命令不能为空");

        // 安全校验
        var lower = command.ToLowerInvariant();
        foreach (var blocked in BlockedCommands)
        {
            if (lower.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error($"命令被安全策略阻止: 包含危险操作 '{blocked}'");
        }

        var workDir = parameters.TryGetProperty("working_directory", out var wdEl)
            ? wdEl.GetString()
            : null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(55));

            var psi = new ProcessStartInfo
            {
                FileName = ShellDetector.GetPowerShellPath(),
                Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir))
                psi.WorkingDirectory = workDir;

            using var process = Process.Start(psi);
            if (process is null)
                return ToolResult.Error("无法启动 PowerShell 进程");

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (stdout.Length > 30_000)
                stdout = string.Concat(stdout.AsSpan(0, 30_000), "\n... (输出已截断)");

            var sb = new StringBuilder();
            sb.AppendLine($"[退出码: {process.ExitCode}]");

            if (!string.IsNullOrWhiteSpace(stdout))
                sb.AppendLine(stdout);

            if (!string.IsNullOrWhiteSpace(stderr))
                sb.Append("[stderr] ").AppendLine(stderr);

            return ToolResult.Ok(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("命令执行超时 (55秒)");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"执行失败: {ex.Message}");
        }
    }
}
