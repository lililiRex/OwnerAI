using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.FileSystem;

/// <summary>
/// 文件搜索工具
/// </summary>
[Tool("search_files", "在指定目录中搜索文件",
    SecurityLevel = ToolSecurityLevel.ReadOnly,
    TimeoutSeconds = 30)]
public sealed class SearchFilesTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => true;

    public ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("query", out var queryEl))
            return ValueTask.FromResult(ToolResult.Error("缺少参数: query"));

        var query = queryEl.GetString();
        if (string.IsNullOrWhiteSpace(query))
            return ValueTask.FromResult(ToolResult.Error("搜索关键词不能为空"));

        var directory = parameters.TryGetProperty("directory", out var dirEl)
            ? dirEl.GetString() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!Directory.Exists(directory))
            return ValueTask.FromResult(ToolResult.Error($"目录不存在: {directory}"));

        try
        {
            var pattern = $"*{query}*";
            var files = Directory.EnumerateFiles(directory, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 5,
            })
            .Take(50)
            .ToList();

            if (files.Count == 0)
                return ValueTask.FromResult(ToolResult.Ok($"未找到匹配 '{query}' 的文件"));

            var result = string.Join('\n', files.Select(f =>
            {
                var info = new FileInfo(f);
                return $"  {f} ({info.Length / 1024.0:F1} KB, {info.LastWriteTime:yyyy-MM-dd HH:mm})";
            }));

            return ValueTask.FromResult(ToolResult.Ok($"找到 {files.Count} 个文件:\n{result}"));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolResult.Error($"搜索失败: {ex.Message}"));
        }
    }
}
