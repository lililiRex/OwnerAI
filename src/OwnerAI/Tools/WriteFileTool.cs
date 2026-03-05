using OwnerAI.Models;

namespace OwnerAI.Tools;

public class WriteFileTool : IToolHandler
{
    public string Name => "write_file";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "创建或覆盖指定路径的文件，写入文本内容",
            Parameters = new()
            {
                Properties = new()
                {
                    ["path"] = new() { Type = "string", Description = "文件的完整路径或相对路径" },
                    ["Content"] = new() { Type = "string", Description = "要写入文件的文本内容" }
                },
                Required = ["path", "Content"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("path", out var pathObj) || pathObj is null)
            return "错误：缺少 path 参数";
        if (!parameters.TryGetValue("Content", out var contentObj) || contentObj is null)
            return "错误：缺少 Content 参数";

        var path = pathObj.ToString()!;
        var content = contentObj.ToString()!;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, content);
            var info = new FileInfo(path);
            return $"文件已成功写入：{path}（大小：{info.Length} 字节）";
        }
        catch (UnauthorizedAccessException)
        {
            return $"错误：无权限写入文件：{path}";
        }
        catch (Exception ex)
        {
            return $"写入文件失败：{ex.Message}";
        }
    }
}
