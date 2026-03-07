using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.SystemTools;

/// <summary>
/// 系统信息工具
/// </summary>
[Tool("system_info", "获取当前系统的基本信息",
    SecurityLevel = ToolSecurityLevel.ReadOnly,
    TimeoutSeconds = 5)]
public sealed class SystemInfoTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => true;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 系统信息");
        sb.Append("- 操作系统: ").AppendLine(RuntimeInformation.OSDescription);
        sb.Append("- 架构: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
        sb.Append("- 运行时: ").AppendLine(RuntimeInformation.FrameworkDescription);
        sb.Append("- 计算机名: ").AppendLine(Environment.MachineName);
        sb.Append("- 用户名: ").AppendLine(Environment.UserName);
        sb.Append("- 处理器数: ").AppendLine(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        sb.Append("- 系统目录: ").AppendLine(Environment.SystemDirectory);
        sb.Append("- 当前目录: ").AppendLine(Environment.CurrentDirectory);
        sb.Append("- 系统运行时间: ").AppendLine(
            TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture));

        using var process = global::System.Diagnostics.Process.GetCurrentProcess();
        sb.Append("- 进程内存: ")
          .Append((process.WorkingSet64 / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture))
          .AppendLine(" MB");

        return ValueTask.FromResult(ToolResult.Ok(sb.ToString()));
    }
}
