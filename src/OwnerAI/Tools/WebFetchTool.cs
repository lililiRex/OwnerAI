using HtmlAgilityPack;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class WebFetchTool : IToolHandler
{
    private readonly HttpClient _http;

    public string Name => "web_fetch";

    public WebFetchTool(HttpClient http)
    {
        _http = http;
    }

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "获取指定网页的结构化内容，包括标题、正文文本、图片链接和视频链接",
            Parameters = new()
            {
                Properties = new()
                {
                    ["Url"] = new() { Type = "string", Description = "要获取内容的网页 URL" }
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

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return "错误：无效的 URL，仅支持 http 和 https";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Remove script, style, nav, footer nodes
            var removeNodes = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//aside|//noscript");
            if (removeNodes != null)
                foreach (var node in removeNodes.ToList())
                    node.Remove();

            var result = new System.Text.StringBuilder();

            // Title
            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            var title = titleNode?.InnerText.Trim() ?? "";
            result.AppendLine($"## 标题\n{title}");
            result.AppendLine();

            // Meta description
            var metaDesc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
            if (metaDesc != null)
            {
                var desc = metaDesc.GetAttributeValue("content", "").Trim();
                if (!string.IsNullOrEmpty(desc))
                {
                    result.AppendLine($"## 描述\n{desc}");
                    result.AppendLine();
                }
            }

            // Main content
            var bodyNode = doc.DocumentNode.SelectSingleNode("//article") ??
                           doc.DocumentNode.SelectSingleNode("//main") ??
                           doc.DocumentNode.SelectSingleNode("//div[@id='content']") ??
                           doc.DocumentNode.SelectSingleNode("//div[@class='content']") ??
                           doc.DocumentNode.SelectSingleNode("//body");

            if (bodyNode != null)
            {
                var text = ExtractText(bodyNode);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.AppendLine("## 正文");
                    result.AppendLine(text.Length > 5000 ? text[..5000] + "\n...(内容已截断)" : text);
                    result.AppendLine();
                }
            }

            // Images
            var images = doc.DocumentNode.SelectNodes("//img[@src]");
            if (images != null && images.Count > 0)
            {
                result.AppendLine("## 图片");
                int imgCount = 0;
                foreach (var img in images.Take(10))
                {
                    var src = img.GetAttributeValue("src", "");
                    var alt = img.GetAttributeValue("alt", "");
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    if (src.StartsWith("//")) src = "https:" + src;
                    if (!src.StartsWith("http")) src = new Uri(new Uri(url), src).ToString();
                    result.AppendLine($"- ![{alt}]({src})");
                    imgCount++;
                }
                if (imgCount > 0) result.AppendLine();
            }

            // Videos
            var videoSrcs = new List<string>();
            var iframes = doc.DocumentNode.SelectNodes("//iframe[@src]");
            if (iframes != null)
                foreach (var iframe in iframes)
                {
                    var src = iframe.GetAttributeValue("src", "");
                    if (src.Contains("youtube") || src.Contains("bilibili") || src.Contains("youku") ||
                        src.Contains("video") || src.Contains("player"))
                        videoSrcs.Add(src.StartsWith("//") ? "https:" + src : src);
                }

            var videoTags = doc.DocumentNode.SelectNodes("//video/source[@src]|//video[@src]");
            if (videoTags != null)
                foreach (var v in videoTags)
                {
                    var src = v.GetAttributeValue("src", "");
                    if (!string.IsNullOrWhiteSpace(src)) videoSrcs.Add(src);
                }

            if (videoSrcs.Count > 0)
            {
                result.AppendLine("## 视频");
                foreach (var v in videoSrcs.Distinct().Take(5))
                    result.AppendLine($"- {v}");
                result.AppendLine();
            }

            return result.ToString();
        }
        catch (HttpRequestException ex)
        {
            return $"网络请求失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"获取网页失败：{ex.Message}";
        }
    }

    private static string ExtractText(HtmlNode node)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                var text = HtmlEntity.DeEntitize(child.InnerText).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }
            else if (child.NodeType == HtmlNodeType.Element)
            {
                var tag = child.Name.ToLower();
                if (tag is "script" or "style" or "noscript") continue;

                var childText = ExtractText(child);
                if (!string.IsNullOrWhiteSpace(childText))
                {
                    if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                        sb.AppendLine($"\n### {childText.Trim()}");
                    else if (tag is "p" or "li" or "div" or "article" or "section")
                        sb.AppendLine(childText.Trim());
                    else
                        sb.Append(childText);
                }
            }
        }
        return sb.ToString();
    }
}
