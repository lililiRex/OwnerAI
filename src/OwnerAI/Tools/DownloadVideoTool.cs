using System.Diagnostics;
using System.Runtime.InteropServices;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class DownloadVideoTool : IToolHandler
{
    public string Name => "download_video";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "下载 YouTube、Bilibili 等平台的视频（依赖 yt-dlp 工具）",
            Parameters = new()
            {
                Properties = new()
                {
                    ["Url"] = new() { Type = "string", Description = "视频页面的 URL" },
                    ["save_directory"] = new()
                    {
                        Type = "string",
                        Description = "视频保存的目录路径（默认为当前目录下的 Downloads 文件夹）"
                    },
                    ["quality"] = new()
                    {
                        Type = "string",
                        Description = "视频画质：best（最高画质）、1080p、720p、480p、360p、audio（仅音频）",
                        Enum = ["best", "1080p", "720p", "480p", "360p", "audio"]
                    }
                },
                Required = ["Url"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("Url", out var urlObj) || urlObj is null)
            return "错误：缺少 Url 参数";

        var url = urlObj.ToString()!;
        var saveDir = parameters.TryGetValue("save_directory", out var dirObj) && dirObj is not null
            ? dirObj.ToString()!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var quality = parameters.TryGetValue("quality", out var qualObj) && qualObj is not null
            ? qualObj.ToString()!
            : "best";

        // Check if yt-dlp is available
        var ytDlp = await FindYtDlpAsync();
        if (ytDlp == null)
            return "错误：未找到 yt-dlp，请先安装：https://github.com/yt-dlp/yt-dlp#installation";

        if (!Directory.Exists(saveDir))
        {
            try { Directory.CreateDirectory(saveDir); }
            catch (Exception ex) { return $"错误：无法创建目录 {saveDir}：{ex.Message}"; }
        }

        // Build yt-dlp arguments
        var formatArg = quality switch
        {
            "1080p" => "-f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]\"",
            "720p" => "-f \"bestvideo[height<=720]+bestaudio/best[height<=720]\"",
            "480p" => "-f \"bestvideo[height<=480]+bestaudio/best[height<=480]\"",
            "360p" => "-f \"bestvideo[height<=360]+bestaudio/best[height<=360]\"",
            "audio" => "-f bestaudio -x --audio-format mp3",
            _ => "-f \"bestvideo+bestaudio/best\""
        };

        var outputTemplate = Path.Combine(saveDir, "%(title)s.%(ext)s");
        var args = $"{formatArg} --merge-output-format mp4 -o \"{outputTemplate}\" \"{url}\"";

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ytDlp,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var completionTask = Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(10));
            var completed = await Task.WhenAny(completionTask, timeoutTask);

            if (completed == timeoutTask)
            {
                try { process.Kill(); } catch { }
                return "错误：视频下载超时（10分钟）";
            }

            var output = await outputTask;
            var error = await errorTask;
            var exitCode = process.ExitCode;

            if (exitCode == 0)
            {
                // Extract filename from output
                var destLine = output.Split('\n')
                    .FirstOrDefault(l => l.Contains("[Merger]") || l.Contains("Destination:") || l.Contains("[download] Destination:"));

                return $"视频下载成功！\n保存目录：{saveDir}\n{(destLine != null ? $"文件：{destLine.Trim()}" : "")}\n\n下载输出：\n{output.TakeLast(500)}";
            }
            else
            {
                return $"视频下载失败（退出码：{exitCode}）：\n{error}";
            }
        }
        catch (Exception ex)
        {
            return $"执行 yt-dlp 失败：{ex.Message}";
        }
    }

    private static async Task<string?> FindYtDlpAsync()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "yt-dlp.exe", "yt-dlp" }
            : new[] { "yt-dlp" };

        foreach (var candidate in candidates)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                process.Start();
                await process.WaitForExitAsync();
                if (process.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return null;
    }
}

file static class StringExtensions
{
    public static string TakeLast(this string s, int count) =>
        s.Length <= count ? s : "..." + s[^count..];
}
