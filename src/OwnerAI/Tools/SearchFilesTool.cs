using OwnerAI.Models;

namespace OwnerAI.Tools;

public class SearchFilesTool : IToolHandler
{
    public string Name => "search_files";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "在指定目录中按名称搜索文件（支持通配符，如 *.txt）",
            Parameters = new()
            {
                Properties = new()
                {
                    ["query"] = new() { Type = "string", Description = "文件名搜索词或通配符模式（如 *.txt、report*）" },
                    ["directory"] = new() { Type = "string", Description = "搜索的起始目录路径，默认为当前目录" }
                },
                Required = ["query"]
            }
        }
    };

    public Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("query", out var queryObj) || queryObj is null)
            return Task.FromResult("错误：缺少 query 参数");

        var query = queryObj.ToString()!;
        var directory = parameters.TryGetValue("directory", out var dirObj) && dirObj is not null
            ? dirObj.ToString()!
            : Directory.GetCurrentDirectory();

        // Ensure the query is treated as a pattern
        var pattern = query.Contains('*') || query.Contains('?') ? query : $"*{query}*";

        try
        {
            if (!Directory.Exists(directory))
                return Task.FromResult($"错误：目录不存在：{directory}");

            var files = Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Take(100)
                .ToList();

            if (files.Count == 0)
                return Task.FromResult($"在 {directory} 中未找到匹配 \"{query}\" 的文件");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"在 {directory} 中搜索 \"{query}\" 找到 {files.Count} 个文件：");
            sb.AppendLine();

            foreach (var file in files)
            {
                var info = new FileInfo(file);
                var size = info.Length < 1024 ? $"{info.Length}B"
                    : info.Length < 1024 * 1024 ? $"{info.Length / 1024}KB"
                    : $"{info.Length / 1024 / 1024}MB";
                sb.AppendLine($"  📄 {file} ({size})");
            }

            if (files.Count == 100)
                sb.AppendLine("\n（结果已截断，仅显示前 100 个）");

            return Task.FromResult(sb.ToString());
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult($"错误：无权限搜索目录：{directory}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"搜索文件失败：{ex.Message}");
        }
    }
}
