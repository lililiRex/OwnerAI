using OwnerAI.Models;

namespace OwnerAI.Tools;

public class ListDirectoryTool : IToolHandler
{
    public string Name => "list_directory";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "列出指定目录下的所有文件和子目录",
            Parameters = new()
            {
                Properties = new()
                {
                    ["path"] = new() { Type = "string", Description = "要列出内容的目录路径" }
                },
                Required = ["path"]
            }
        }
    };

    public Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("path", out var pathObj) || pathObj is null)
            return Task.FromResult("错误：缺少 path 参数");

        var path = pathObj.ToString()!;

        try
        {
            if (!Directory.Exists(path))
                return Task.FromResult($"错误：目录不存在：{path}");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"目录内容：{path}");
            sb.AppendLine();

            var dirs = Directory.GetDirectories(path).OrderBy(d => d).ToArray();
            var files = Directory.GetFiles(path).OrderBy(f => f).ToArray();

            sb.AppendLine($"📁 子目录（{dirs.Length} 个）：");
            foreach (var dir in dirs)
                sb.AppendLine($"  📁 {Path.GetFileName(dir)}/");

            sb.AppendLine();
            sb.AppendLine($"📄 文件（{files.Length} 个）：");
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                var size = info.Length < 1024 ? $"{info.Length}B"
                    : info.Length < 1024 * 1024 ? $"{info.Length / 1024}KB"
                    : $"{info.Length / 1024 / 1024}MB";
                sb.AppendLine($"  📄 {Path.GetFileName(file)} ({size}, {info.LastWriteTime:yyyy-MM-dd HH:mm})");
            }

            return Task.FromResult(sb.ToString());
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult($"错误：无权限访问目录：{path}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"列出目录失败：{ex.Message}");
        }
    }
}
