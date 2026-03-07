using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Download;

/// <summary>
/// YouTube/Bilibili 视频下载工具 — 使用 yt-dlp 下载视频
/// </summary>
[Tool("download_video", "从 YouTube、Bilibili 等视频平台下载视频。需要系统安装 yt-dlp",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 600)]
public sealed class DownloadVideoTool : IOwnerAITool
{
    private static readonly string[] SupportedDomains =
    [
        "youtube.com", "youtu.be", "bilibili.com", "b23.tv",
        "v.qq.com", "youku.com", "ixigua.com", "douyin.com",
        "vimeo.com", "dailymotion.com", "twitch.tv",
    ];

    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("url", out var urlEl))
            return ToolResult.Error("缺少参数: url");

        var url = urlEl.GetString();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ToolResult.Error("无效的视频 URL");

        // 校验是否为支持的视频平台
        var host = uri.Host.ToLowerInvariant();
        if (!SupportedDomains.Any(d => host.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Error($"不支持的视频平台: {host}。支持: YouTube, Bilibili, 腾讯视频, 优酷, 西瓜视频, 抖音, Vimeo 等");

        // 检查 yt-dlp 是否可用
        var ytdlpPath = await FindYtDlpAsync(ct);
        if (ytdlpPath is null)
            return ToolResult.Error("未检测到 yt-dlp。请先安装: winget install yt-dlp 或 pip install yt-dlp");

        // 保存目录
        var outputDir = parameters.TryGetProperty("save_directory", out var dirEl) ? dirEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        Directory.CreateDirectory(outputDir);

        // 画质选择
        var quality = parameters.TryGetProperty("quality", out var qEl) ? qEl.GetString() : "best";
        var formatArg = quality?.ToLowerInvariant() switch
        {
            "720p" => "-f \"bestvideo[height<=720]+bestaudio/best[height<=720]\"",
            "1080p" => "-f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]\"",
            "480p" => "-f \"bestvideo[height<=480]+bestaudio/best[height<=480]\"",
            "audio" => "-x --audio-format mp3",
            _ => "-f \"bestvideo+bestaudio/best\"",
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(9));

            var outputTemplate = Path.Combine(outputDir, "%(title)s.%(ext)s");
            var arguments = $"{formatArg} --merge-output-format mp4 -o \"{outputTemplate}\" --no-playlist --encoding utf-8 \"{url}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ytdlpPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return ToolResult.Error("无法启动 yt-dlp");

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (error.Length > 500) error = string.Concat(error.AsSpan(0, 500), "...");
                return ToolResult.Error($"yt-dlp 退出码 {process.ExitCode}: {error}");
            }

            // 从输出中提取文件名
            var destMatch = Regex.Match(stdout, @"\[download\] Destination: (.+)$|Merging formats into ""(.+)""|\[download\] (.+) has already been downloaded", RegexOptions.Multiline);
            var filePath = destMatch.Success
                ? (destMatch.Groups[1].Value + destMatch.Groups[2].Value + destMatch.Groups[3].Value).Trim()
                : outputDir;

            var sb = new StringBuilder("视频下载完成\n");
            sb.Append("保存位置: ").AppendLine(string.IsNullOrWhiteSpace(filePath) ? outputDir : filePath);
            sb.Append("画质: ").AppendLine(quality ?? "best");

            return ToolResult.Ok(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("视频下载超时 (9分钟)");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"下载失败: {ex.Message}");
        }
    }

    private static async Task<string?> FindYtDlpAsync(CancellationToken ct)
    {
        string[] candidates = ["yt-dlp", "yt-dlp.exe"];

        foreach (var name in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process is not null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0) return name;
                }
            }
            catch { }
        }

        return null;
    }
}
