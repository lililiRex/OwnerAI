using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.FileSystem;

/// <summary>
/// 读取文件工具
/// </summary>
[Tool("read_file", "读取指定路径文件的内容",
    SecurityLevel = ToolSecurityLevel.ReadOnly,
    TimeoutSeconds = 10)]
public sealed class ReadFileTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("path", out var pathElement))
            return ToolResult.Error("缺少参数: path");

        var path = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return ToolResult.Error("路径不能为空");

        if (!File.Exists(path))
            return ToolResult.Error($"文件不存在: {path}");

        try
        {
            var content = await File.ReadAllTextAsync(path, ct);
            // 限制输出大小
            if (content.Length > 50_000)
            {
                content = string.Concat(content.AsSpan(0, 50_000), "\n\n... (文件过大，已截断)");
            }
            return ToolResult.Ok(content);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"读取文件失败: {ex.Message}");
        }
    }
}
