using System.Diagnostics;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class ProcessListTool : IToolHandler
{
    public string Name => "process_list";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "列出当前系统中正在运行的进程，包括进程 ID、名称和内存占用",
            Parameters = new()
            {
                Properties = new()
            }
        }
    };

    public Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        try
        {
            var processes = Process.GetProcesses()
                .Select(p =>
                {
                    long mem = 0;
                    try { mem = p.WorkingSet64; } catch { }
                    return new { p.Id, p.ProcessName, MemoryMB = mem / 1024.0 / 1024 };
                })
                .OrderByDescending(p => p.MemoryMB)
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"当前运行进程（共 {processes.Count} 个）：");
            sb.AppendLine();
            sb.AppendLine($"{"PID",-8} {"内存(MB)",-12} {"进程名"}");
            sb.AppendLine(new string('-', 50));

            foreach (var p in processes.Take(50))
                sb.AppendLine($"{p.Id,-8} {p.MemoryMB,-12:F1} {p.ProcessName}");

            if (processes.Count > 50)
                sb.AppendLine($"\n（仅显示内存占用最高的 50 个进程，共 {processes.Count} 个）");

            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"获取进程列表失败：{ex.Message}");
        }
    }
}
