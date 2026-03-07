using System.Text;
using System.Text.Json;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.Web;

/// <summary>
/// 网页搜索工具 — 使用 Bing 搜索并提取结构化结果（标题 + 链接 + 摘要）
/// </summary>
[Tool("web_search", "使用 Bing 搜索信息，返回结构化搜索结果（标题、链接、摘要）",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 30)]
public sealed class WebSearchTool : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("query", out var queryEl))
            return ToolResult.Error("缺少参数: query");

        var query = queryEl.GetString();
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error("搜索关键词不能为空");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            var encodedQuery = Uri.EscapeDataString(query);
            var searchUrl = $"https://www.bing.com/search?q={encodedQuery}&setlang=zh-Hans";
            var searchUri = new Uri(searchUrl);

            var html = await WebFetchTool.FetchHtmlAsync(searchUri, cts.Token);

            if (string.IsNullOrWhiteSpace(html))
                return ToolResult.Error("搜索结果为空");

            var results = HtmlContentExtractor.ExtractSearchResults(html);

            if (results.Length > 15_000)
                results = string.Concat(results.AsSpan(0, 15_000), "\n... (已截断)");

            return ToolResult.Ok($"# 搜索: {query}\n\n{results}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("搜索超时");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"搜索失败: {ex.Message}");
        }
    }
}
