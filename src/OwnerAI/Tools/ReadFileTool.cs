using OwnerAI.Models;

namespace OwnerAI.Tools;

public class ReadFileTool : IToolHandler
{
    public string Name => "read_file";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "读取指定路径文件的内容",
            Parameters = new()
            {
                Properties = new()
                {
                    ["path"] = new() { Type = "string", Description = "文件的完整路径或相对路径" }
                },
                Required = ["path"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("path", out var pathObj) || pathObj is null)
            return "错误：缺少 path 参数";

        var path = pathObj.ToString()!;

        try
        {
            if (!File.Exists(path))
                return $"错误：文件不存在：{path}";

            var info = new FileInfo(path);
            if (info.Length > 10 * 1024 * 1024)
                return $"错误：文件过大（{info.Length / 1024 / 1024}MB），仅支持读取 10MB 以内的文件";

            var content = await File.ReadAllTextAsync(path);
            return $"文件内容（{path}）：\n\n{content}";
        }
        catch (UnauthorizedAccessException)
        {
            return $"错误：无权限读取文件：{path}";
        }
        catch (Exception ex)
        {
            return $"读取文件失败：{ex.Message}";
        }
    }
}
