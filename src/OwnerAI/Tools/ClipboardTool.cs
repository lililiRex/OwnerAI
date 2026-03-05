using System.Runtime.InteropServices;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class ClipboardTool : IToolHandler
{
    public string Name => "clipboard";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "读取或写入系统剪贴板内容",
            Parameters = new()
            {
                Properties = new()
                {
                    ["Action"] = new()
                    {
                        Type = "string",
                        Description = "操作类型：read（读取剪贴板）或 write（写入剪贴板）",
                        Enum = ["read", "write"]
                    },
                    ["Content"] = new()
                    {
                        Type = "string",
                        Description = "写入剪贴板的文本内容（Action 为 write 时必填）"
                    }
                },
                Required = ["Action"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("Action", out var actionObj) || actionObj is null)
            return "错误：缺少 Action 参数";

        var action = actionObj.ToString()!.ToLower().Trim();

        if (action == "read")
            return await ReadClipboardAsync();

        if (action == "write")
        {
            if (!parameters.TryGetValue("Content", out var contentObj) || contentObj is null)
                return "错误：写入剪贴板时缺少 Content 参数";
            return await WriteClipboardAsync(contentObj.ToString()!);
        }

        return $"错误：未知的 Action '{action}'，有效值为 read 或 write";
    }

    private static Task<string> ReadClipboardAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Task.FromResult(ReadWindowsClipboard());

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return ReadProcessOutputAsync("pbpaste", "");

            // Linux
            return ReadProcessOutputAsync("xclip", "-selection clipboard -o");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"读取剪贴板失败：{ex.Message}");
        }
    }

    private static Task<string> WriteClipboardAsync(string content)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Task.FromResult(WriteWindowsClipboard(content));

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return WriteProcessInputAsync("pbcopy", "", content);

            // Linux
            return WriteProcessInputAsync("xclip", "-selection clipboard", content);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"写入剪贴板失败：{ex.Message}");
        }
    }

    private static string ReadWindowsClipboard()
    {
        // Use PowerShell to read clipboard on Windows
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Get-Clipboard\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.TrimEnd('\r', '\n');
    }

    private static string WriteWindowsClipboard(string content)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -Command \"Set-Clipboard -Value '{content.Replace("'", "''")}'\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        process.WaitForExit();
        return "已成功写入剪贴板";
    }

    private static async Task<string> ReadProcessOutputAsync(string command, string args)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private static async Task<string> WriteProcessInputAsync(string command, string args, string input)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        process.Start();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return "已成功写入剪贴板";
    }
}
