using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OwnerAI.Agent.Tools.Shell;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Web;

/// <summary>
/// 网页内容获取工具 — 提取标题、正文、配图、视频等结构化信息
/// </summary>
[Tool("web_fetch", "获取指定网页的结构化内容（标题、正文、图片、视频），可用于查询资料、获取文档等",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 30)]
public sealed class WebFetchTool : IOwnerAITool
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

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            var html = await FetchHtmlAsync(uri, cts.Token);
            if (string.IsNullOrWhiteSpace(html))
                return ToolResult.Error("网页内容为空或请求失败");

            var page = HtmlContentExtractor.Extract(html, uri.AbsoluteUri);

            // 提取媒体 URL — 仅临时展示在消息框中，不保存到本地
            var mediaUrls = new List<ToolMediaUrl>();
            foreach (var img in page.Images)
                mediaUrls.Add(new ToolMediaUrl(img.Url, ToolMediaKind.Image, img.Alt));
            foreach (var video in page.Videos)
                mediaUrls.Add(new ToolMediaUrl(video.Url, ToolMediaKind.Video, video.Platform));

            return ToolResult.Ok(page.Format()) with
            {
                MediaUrls = mediaUrls.Count > 0 ? mediaUrls : null,
            };
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("请求超时");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"获取网页失败: {ex.Message}");
        }
    }

    internal static async Task<string> FetchHtmlAsync(Uri uri, CancellationToken ct)
    {
        var psCommand = $"(Invoke-WebRequest -Uri '{uri.AbsoluteUri}' -UseBasicParsing -TimeoutSec 20).Content";

        var psi = new ProcessStartInfo
        {
            FileName = ShellDetector.GetPowerShellPath(),
            Arguments = $"-NoProfile -NonInteractive -Command \"{psCommand}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 PowerShell");

        var html = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return html;
    }
}
