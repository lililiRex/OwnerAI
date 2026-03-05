using System.Diagnostics;
using System.Runtime.InteropServices;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class RunCommandTool : IToolHandler
{
    private static readonly HashSet<string> BlacklistedPatterns =
    [
        // Destructive filesystem operations
        "rm -rf /", "rm -rf ~", "del /f /s /q c:\\", "format c:",
        "rd /s /q c:\\", "rmdir /s /q c:\\",
        // Registry destruction
        "reg delete hklm", "reg delete hkcu",
        // Network/system disruption
        "netsh firewall", "sc stop", "sc delete",
        // Dangerous Windows commands
        "cipher /w:c", "sfc /scannow",
        // Fork bomb patterns
        ":(){ :|:& };:", "powershell -c \"while\"",
        // Shutdown/restart
        "shutdown /s", "shutdown /r", "restart-computer",
        // Process killing that could be harmful
        "stop-process -name explorer", "taskkill /f /im explorer"
    ];

    public string Name => "run_command";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "通过 PowerShell（Windows）或 Shell（Linux/macOS）执行命令，安全黑名单防止危险操作",
            Parameters = new()
            {
                Properties = new()
                {
                    ["command"] = new() { Type = "string", Description = "要执行的命令" },
                    ["working_directory"] = new()
                    {
                        Type = "string",
                        Description = "命令执行的工作目录（可选，默认为当前目录）"
                    }
                },
                Required = ["command"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("command", out var cmdObj) || cmdObj is null)
            return "错误：缺少 command 参数";

        var command = cmdObj.ToString()!;
        var workingDir = parameters.TryGetValue("working_directory", out var wdObj) && wdObj is not null
            ? wdObj.ToString()!
            : Directory.GetCurrentDirectory();

        // Security check
        var cmdLower = command.ToLower();
        foreach (var pattern in BlacklistedPatterns)
        {
            if (cmdLower.Contains(pattern))
                return $"安全拒绝：命令包含危险操作 \"{pattern}\"，已阻止执行";
        }

        if (!Directory.Exists(workingDir))
            return $"错误：工作目录不存在：{workingDir}";

        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.FileName = "powershell.exe";
                psi.Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"";
            }
            else
            {
                psi.FileName = "/bin/bash";
                psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var completionTask = Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            var completed = await Task.WhenAny(completionTask, timeoutTask);

            if (completed == timeoutTask)
            {
                try { process.Kill(); } catch { }
                return "错误：命令执行超时（30秒）";
            }

            var output = await outputTask;
            var error = await errorTask;
            var exitCode = process.ExitCode;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"命令：{command}");
            sb.AppendLine($"退出码：{exitCode}");

            if (!string.IsNullOrWhiteSpace(output))
            {
                sb.AppendLine("标准输出：");
                sb.AppendLine(output.Length > 8000 ? output[..8000] + "\n...(输出已截断)" : output);
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                sb.AppendLine("错误输出：");
                sb.AppendLine(error.Length > 2000 ? error[..2000] + "\n...(输出已截断)" : error);
            }

            if (string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(error))
                sb.AppendLine("（无输出）");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"执行命令失败：{ex.Message}";
        }
    }
}
