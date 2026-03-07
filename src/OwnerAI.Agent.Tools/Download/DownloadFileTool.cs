using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Download;

/// <summary>
/// 文件下载工具 — 从 URL 下载图片、视频、文件到本地
/// </summary>
[Tool("download_file", "从 URL 下载文件（图片、视频、文档等）到本地指定路径",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 300)]
public sealed class DownloadFileTool : IOwnerAITool
{
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
            return ToolResult.Error("无效的 URL");

        if (uri.Scheme is not ("http" or "https"))
            return ToolResult.Error("仅支持 http/https 协议");

        // 目标路径（默认保存到 Downloads 文件夹）
        var savePath = parameters.TryGetProperty("save_path", out var pathEl) ? pathEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            var downloadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var fileName = GetFileNameFromUrl(uri);
            savePath = Path.Combine(downloadsDir, fileName);
        }

        // 确保目录存在
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(280));

            // 使用 PowerShell 下载（避免额外依赖）
            var psCommand = $"Invoke-WebRequest -Uri '{uri.AbsoluteUri}' -OutFile '{savePath.Replace("'", "''")}' -TimeoutSec 240";
            var shell = Shell.ShellDetector.GetPowerShellPath();

            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"-NoProfile -NonInteractive -Command \"{psCommand}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return ToolResult.Error("无法启动下载进程");

            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
                return ToolResult.Error($"下载失败: {stderr}");

            if (!File.Exists(savePath))
                return ToolResult.Error("下载完成但文件未找到");

            var fileInfo = new FileInfo(savePath);
            var sizeStr = fileInfo.Length switch
            {
                < 1024 => $"{fileInfo.Length} B",
                < 1024 * 1024 => $"{fileInfo.Length / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{fileInfo.Length / (1024.0 * 1024):F1} MB",
                _ => $"{fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB",
            };

            return ToolResult.Ok($"下载完成\n路径: {savePath}\n大小: {sizeStr}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("下载超时");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"下载失败: {ex.Message}");
        }
    }

    private static string GetFileNameFromUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        var name = Path.GetFileName(path);
        if (!string.IsNullOrWhiteSpace(name) && name.Contains('.'))
            return name.Length > 100 ? name[..100] : name;

        // 无法从 URL 推断文件名
        return $"download_{DateTime.Now:yyyyMMdd_HHmmss}";
    }
}
