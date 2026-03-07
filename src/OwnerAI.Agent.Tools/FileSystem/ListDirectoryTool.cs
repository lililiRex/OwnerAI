using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.FileSystem;

/// <summary>
/// 列出目录工具
/// </summary>
[Tool("list_directory", "列出指定目录下的文件和子目录",
    SecurityLevel = ToolSecurityLevel.ReadOnly,
    TimeoutSeconds = 10)]
public sealed class ListDirectoryTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => true;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("path", out var pathElement))
            return ValueTask.FromResult(ToolResult.Error("缺少参数: path"));

        var path = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ValueTask.FromResult(ToolResult.Error("路径不能为空"));

        if (!Directory.Exists(path))
            return ValueTask.FromResult(ToolResult.Error($"目录不存在: {path}"));

        try
        {
            var sb = new StringBuilder();
            var dirs = Directory.GetDirectories(path);
            var files = Directory.GetFiles(path);

            sb.AppendLine($"目录: {path}");
            sb.AppendLine($"子目录 ({dirs.Length}):");
            foreach (var dir in dirs.Take(100))
            {
                sb.AppendLine($"  📁 {Path.GetFileName(dir)}/");
            }

            sb.AppendLine($"文件 ({files.Length}):");
            foreach (var file in files.Take(100))
            {
                var info = new FileInfo(file);
                sb.AppendLine($"  📄 {info.Name} ({FormatSize(info.Length)})");
            }

            if (dirs.Length > 100 || files.Length > 100)
                sb.AppendLine("... (结果已截断)");

            return ValueTask.FromResult(ToolResult.Ok(sb.ToString()));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Error($"列出目录失败: {ex.Message}"));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };
}
