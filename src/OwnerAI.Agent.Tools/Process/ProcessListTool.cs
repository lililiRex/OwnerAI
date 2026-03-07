using System.Text.Json;
using OwnerAI.Shared.Abstractions;
using SysProcess = global::System.Diagnostics.Process;

namespace OwnerAI.Agent.Tools.ProcessTools;

/// <summary>
/// 进程列表工具
/// </summary>
[Tool("process_list", "列出当前运行的进程",
    SecurityLevel = ToolSecurityLevel.ReadOnly,
    TimeoutSeconds = 10)]
public sealed class ProcessListTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => true;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        try
        {
            var processes = SysProcess.GetProcesses()
                .OrderByDescending(p =>
                {
                    try { return p.WorkingSet64; }
                    catch { return 0L; }
                })
                .Take(30)
                .Select(FormatProcess);

            var header = $"  {"PID",6} | {"Name",-30} | Memory";
            var result = $"Top 30 进程 (按内存排序):\n{header}\n{new string('-', 60)}\n{string.Join('\n', processes)}";

            return ValueTask.FromResult(ToolResult.Ok(result));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Error($"获取进程列表失败: {ex.Message}"));
        }
    }

    private static string FormatProcess(SysProcess p)
    {
        try
        {
            return $"  {p.Id,6} | {p.ProcessName,-30} | {p.WorkingSet64 / (1024.0 * 1024):F1} MB";
        }
        catch
        {
            return $"  {p.Id,6} | {p.ProcessName,-30} | N/A";
        }
    }
}
