using System.Diagnostics;
using System.Runtime.InteropServices;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class OpenAppTool : IToolHandler
{
    public string Name => "open_app";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "打开应用程序、文件或网址。支持传入可执行文件路径、文档路径或 https:// 链接",
            Parameters = new()
            {
                Properties = new()
                {
                    ["target"] = new()
                    {
                        Type = "string",
                        Description = "要打开的目标：应用路径（如 notepad.exe）、文件路径或网址（如 https://example.com）"
                    },
                    ["arguments"] = new()
                    {
                        Type = "string",
                        Description = "传递给应用程序的命令行参数（可选）"
                    }
                },
                Required = ["target"]
            }
        }
    };

    public Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("target", out var targetObj) || targetObj is null)
            return Task.FromResult("错误：缺少 target 参数");

        var target = targetObj.ToString()!;
        var arguments = parameters.TryGetValue("arguments", out var argsObj) && argsObj is not null
            ? argsObj.ToString()!
            : "";

        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true
            };

            if (target.StartsWith("http://") || target.StartsWith("https://"))
            {
                // Open URL in default browser
                psi.FileName = target;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.FileName = target;
                if (!string.IsNullOrWhiteSpace(arguments))
                    psi.Arguments = arguments;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi.FileName = "open";
                psi.Arguments = string.IsNullOrWhiteSpace(arguments) ? $"\"{target}\"" : $"\"{target}\" {arguments}";
            }
            else
            {
                psi.FileName = "xdg-open";
                psi.Arguments = $"\"{target}\"";
            }

            Process.Start(psi);
            return Task.FromResult($"已打开：{target}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"打开失败：{ex.Message}");
        }
    }
}
