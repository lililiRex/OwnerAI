using System.Text.Json;
using HtmlAgilityPack;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class WebSearchTool : IToolHandler
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public string Name => "web_search";

    public WebSearchTool(HttpClient http, string apiKey, string endpoint)
    {
        _http = http;
        _apiKey = apiKey;
        _endpoint = endpoint;
    }

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "使用 Bing 搜索引擎搜索信息，返回标题、链接和摘要列表",
            Parameters = new()
            {
                Properties = new()
                {
                    ["query"] = new() { Type = "string", Description = "搜索关键词或问题" }
                },
                Required = ["query"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("query", out var queryObj) || queryObj is null)
            return "错误：缺少 query 参数";

        var query = queryObj.ToString()!;

        // If no Bing API key, fall back to HTML scraping
        if (string.IsNullOrWhiteSpace(_apiKey))
            return await FallbackHtmlSearchAsync(query);

        try
        {
            var url = $"{_endpoint}?q={Uri.EscapeDataString(query)}&count=10&mkt=zh-CN";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var results = new System.Text.StringBuilder();
            results.AppendLine($"搜索结果：{query}");
            results.AppendLine();

            if (doc.RootElement.TryGetProperty("webPages", out var webPages) &&
                webPages.TryGetProperty("value", out var items))
            {
                int idx = 1;
                foreach (var item in items.EnumerateArray())
                {
                    var title = item.TryGetProperty("name", out var t) ? t.GetString() : "";
                    var link = item.TryGetProperty("url", out var l) ? l.GetString() : "";
                    var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() : "";
                    results.AppendLine($"{idx}. [{title}]({link})");
                    results.AppendLine($"   {snippet}");
                    results.AppendLine();
                    idx++;
                }
            }

            return results.ToString();
        }
        catch (Exception ex)
        {
            return $"搜索失败：{ex.Message}";
        }
    }

    private async Task<string> FallbackHtmlSearchAsync(string query)
    {
        try
        {
            var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&setlang=zh-CN";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9");

            var response = await _http.SendAsync(request);
            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new System.Text.StringBuilder();
            results.AppendLine($"搜索结果：{query}");
            results.AppendLine();

            // Parse Bing search result items
            var items = doc.DocumentNode.SelectNodes("//li[@class='b_algo']");
            if (items == null)
                return $"搜索 \"{query}\" 未找到结果（提示：配置 Bing API Key 可获得更好效果）";

            int idx = 1;
            foreach (var item in items.Take(10))
            {
                var titleNode = item.SelectSingleNode(".//h2/a");
                var snippetNode = item.SelectSingleNode(".//p");

                var title = titleNode?.InnerText.Trim() ?? "";
                var link = titleNode?.GetAttributeValue("href", "") ?? "";
                var snippet = snippetNode?.InnerText.Trim() ?? "";

                results.AppendLine($"{idx}. [{title}]({link})");
                results.AppendLine($"   {snippet}");
                results.AppendLine();
                idx++;
            }

            return results.ToString();
        }
        catch (Exception ex)
        {
            return $"搜索失败：{ex.Message}";
        }
    }
}
