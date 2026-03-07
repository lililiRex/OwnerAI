using System.Text;
using System.Text.RegularExpressions;

namespace OwnerAI.Agent.Tools.Web;

/// <summary>
/// HTML 结构化内容提取器 — 从原始 HTML 中提取标题、正文、图片、视频等
/// </summary>
internal static partial class HtmlContentExtractor
{
    /// <summary>
    /// 提取网页的结构化内容
    /// </summary>
    public static PageContent Extract(string html, string? pageUrl = null)
    {
        var result = new PageContent();

        // 1. 提取标题
        result.Title = ExtractTitle(html);

        // 2. 提取 meta 描述
        result.Description = ExtractMeta(html, "description")
                          ?? ExtractMeta(html, "og:description");

        // 3. 提取 OG 图片
        var ogImage = ExtractMeta(html, "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage))
            result.Images.Add(new ImageInfo("封面图", ResolveUrl(ogImage, pageUrl)));

        // 4. 移除无用块
        var cleaned = RemoveNoiseTags(html);

        // 5. 提取正文内容（优先 article / main，退而求其次用 body）
        var bodyHtml = ExtractMainContent(cleaned);

        // 6. 从正文 HTML 中提取图片
        foreach (var img in ExtractImages(bodyHtml, pageUrl))
            result.Images.Add(img);

        // 7. 从正文 HTML 中提取视频
        foreach (var video in ExtractVideos(bodyHtml, pageUrl))
            result.Videos.Add(video);

        // 8. 将正文 HTML 转为可读文本
        result.Body = HtmlToReadableText(bodyHtml);

        return result;
    }

    /// <summary>
    /// 提取搜索引擎结果页的结构化条目
    /// </summary>
    public static string ExtractSearchResults(string html)
    {
        var sb = new StringBuilder();

        // Bing 搜索结果在 <li class="b_algo"> 中
        var results = BingResultRegex().Matches(html);
        var index = 0;

        foreach (Match match in results)
        {
            if (++index > 10) break;

            var block = match.Value;

            // 提取标题和链接
            var titleMatch = AnchorRegex().Match(block);
            var title = titleMatch.Success ? StripTags(titleMatch.Groups[2].Value).Trim() : "(无标题)";
            var href = titleMatch.Success ? titleMatch.Groups[1].Value : "";

            // 提取摘要
            var snippetMatch = SnippetRegex().Match(block);
            var snippet = snippetMatch.Success ? StripTags(snippetMatch.Groups[1].Value).Trim() : "";

            sb.AppendLine($"### {index}. {title}");
            if (!string.IsNullOrWhiteSpace(href))
                sb.AppendLine($"链接: {href}");
            if (!string.IsNullOrWhiteSpace(snippet))
                sb.AppendLine(snippet);
            sb.AppendLine();
        }

        // 如果 Bing 格式匹配失败，回退到通用提取
        if (index == 0)
        {
            return FallbackExtract(html);
        }

        return sb.ToString();
    }

    #region Title & Meta

    private static string? ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        if (match.Success)
            return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();

        // 回退: og:title
        return ExtractMeta(html, "og:title");
    }

    private static string? ExtractMeta(string html, string name)
    {
        // <meta name="description" content="..."> 或 <meta property="og:..." content="...">
        var pattern = $@"<meta\s+(?:name|property)\s*=\s*[""']{Regex.Escape(name)}[""']\s+content\s*=\s*[""']([^""']*)[""']";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
            return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();

        // 反向属性顺序
        pattern = $@"<meta\s+content\s*=\s*[""']([^""']*)[""']\s+(?:name|property)\s*=\s*[""']{Regex.Escape(name)}[""']";
        match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    #endregion

    #region Main Content Extraction

    private static string ExtractMainContent(string html)
    {
        // 优先级: <article> > <main> > <div role="main"> > #content > .content > body
        string[] selectors =
        [
            @"<article[^>]*>([\s\S]*?)</article>",
            @"<main[^>]*>([\s\S]*?)</main>",
            @"<div[^>]*role\s*=\s*[""']main[""'][^>]*>([\s\S]*?)</div>",
            @"<div[^>]*(?:id|class)\s*=\s*[""'][^""']*(?:content|article|post-body|entry-content|main-text)[^""']*[""'][^>]*>([\s\S]*?)</div>",
        ];

        foreach (var pattern in selectors)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success && match.Groups[1].Value.Length > 200)
                return match.Groups[1].Value;
        }

        // 回退: 取 body
        var bodyMatch = Regex.Match(html, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return bodyMatch.Success ? bodyMatch.Groups[1].Value : html;
    }

    private static string RemoveNoiseTags(string html)
    {
        // 移除 script, style, nav, header, footer, aside, iframe(非视频), noscript, svg
        string[] noiseTags = ["script", "style", "nav", "header", "footer", "aside", "noscript", "svg", "form"];
        foreach (var tag in noiseTags)
        {
            html = Regex.Replace(html, $@"<{tag}[^>]*>[\s\S]*?</{tag}>", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        // 移除 HTML 注释
        html = Regex.Replace(html, @"<!--[\s\S]*?-->", string.Empty);

        return html;
    }

    #endregion

    #region Images

    private static List<ImageInfo> ExtractImages(string html, string? baseUrl)
    {
        var images = new List<ImageInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ImgRegex().Matches(html))
        {
            var tag = match.Value;

            // 提取 src
            var srcMatch = SrcRegex().Match(tag);
            if (!srcMatch.Success) continue;
            var src = srcMatch.Groups[1].Value;

            // 过滤小图标、跟踪像素、base64 小图
            if (IsNoiseImage(src)) continue;

            var resolved = ResolveUrl(src, baseUrl);
            if (!seen.Add(resolved)) continue;

            // 提取 alt
            var altMatch = AltRegex().Match(tag);
            var alt = altMatch.Success
                ? System.Net.WebUtility.HtmlDecode(altMatch.Groups[1].Value).Trim()
                : null;

            images.Add(new ImageInfo(alt, resolved));

            if (images.Count >= 10) break;
        }

        return images;
    }

    private static bool IsNoiseImage(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return true;

        // 跳过 base64 小图 (通常是 1x1 跟踪像素)
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && src.Length < 200)
            return true;

        // 跳过常见图标/跟踪图
        string[] noise = [
            "pixel", "track", "beacon", "spacer", "blank", "loading",
            "spinner", "logo", "icon", "favicon", "badge", "avatar",
            ".gif", "1x1", "transparent",
        ];

        var lower = src.ToLowerInvariant();
        return noise.Any(n => lower.Contains(n, StringComparison.Ordinal));
    }

    #endregion

    #region Videos

    private static List<VideoInfo> ExtractVideos(string html, string? baseUrl)
    {
        var videos = new List<VideoInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // <video> 标签
        foreach (Match match in VideoTagRegex().Matches(html))
        {
            var tag = match.Value;
            var srcMatch = SrcRegex().Match(tag);
            if (!srcMatch.Success)
            {
                // 检查 <source> 子标签
                var sourceMatch = SourceRegex().Match(tag);
                if (!sourceMatch.Success) continue;
                srcMatch = SrcRegex().Match(sourceMatch.Value);
                if (!srcMatch.Success) continue;
            }

            var src = ResolveUrl(srcMatch.Groups[1].Value, baseUrl);
            if (seen.Add(src))
                videos.Add(new VideoInfo("video", src));
        }

        // <iframe> 视频嵌入 (YouTube, Bilibili, 优酷等)
        foreach (Match match in IframeRegex().Matches(html))
        {
            var srcMatch = SrcRegex().Match(match.Value);
            if (!srcMatch.Success) continue;

            var src = srcMatch.Groups[1].Value;
            if (!IsVideoEmbed(src)) continue;

            var resolved = ResolveUrl(src, baseUrl);
            if (seen.Add(resolved))
            {
                var platform = DetectPlatform(src);
                videos.Add(new VideoInfo(platform, resolved));
            }
        }

        return videos;
    }

    private static bool IsVideoEmbed(string src)
    {
        string[] videoDomains = [
            "youtube.com", "youtu.be", "bilibili.com", "player.bilibili.com",
            "v.qq.com", "player.youku.com", "vimeo.com", "dailymotion.com",
            "douyin.com", "ixigua.com",
        ];
        return videoDomains.Any(d => src.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    private static string DetectPlatform(string src)
    {
        if (src.Contains("bilibili", StringComparison.OrdinalIgnoreCase)) return "bilibili";
        if (src.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            src.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)) return "youtube";
        if (src.Contains("qq.com", StringComparison.OrdinalIgnoreCase)) return "腾讯视频";
        if (src.Contains("youku", StringComparison.OrdinalIgnoreCase)) return "优酷";
        if (src.Contains("douyin", StringComparison.OrdinalIgnoreCase)) return "抖音";
        if (src.Contains("ixigua", StringComparison.OrdinalIgnoreCase)) return "西瓜视频";
        return "video";
    }

    #endregion

    #region HTML → Readable Text

    private static string HtmlToReadableText(string html)
    {
        var text = html;

        // 标题标签 → Markdown 格式
        text = Regex.Replace(text, @"<h1[^>]*>([\s\S]*?)</h1>", m => $"\n# {StripTags(m.Groups[1].Value).Trim()}\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<h2[^>]*>([\s\S]*?)</h2>", m => $"\n## {StripTags(m.Groups[1].Value).Trim()}\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<h3[^>]*>([\s\S]*?)</h3>", m => $"\n### {StripTags(m.Groups[1].Value).Trim()}\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<h[4-6][^>]*>([\s\S]*?)</h[4-6]>", m => $"\n#### {StripTags(m.Groups[1].Value).Trim()}\n", RegexOptions.IgnoreCase);

        // 段落和换行
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?div[^>]*>", "\n", RegexOptions.IgnoreCase);

        // 列表
        text = Regex.Replace(text, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);

        // 链接保留文本
        text = Regex.Replace(text, @"<a[^>]*>([\s\S]*?)</a>", m => StripTags(m.Groups[1].Value), RegexOptions.IgnoreCase);

        // 移除剩余标签
        text = Regex.Replace(text, @"<[^>]+>", " ");

        // HTML 实体解码
        text = System.Net.WebUtility.HtmlDecode(text);

        // 合并空行
        text = Regex.Replace(text, @"\n[ \t]*\n", "\n\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");

        return text.Trim();
    }

    private static string StripTags(string html)
        => Regex.Replace(html, @"<[^>]+>", string.Empty);

    #endregion

    #region Helpers

    private static string ResolveUrl(string url, string? baseUrl)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
            return "https:" + url;

        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return url;

        if (baseUrl is not null && Uri.TryCreate(new Uri(baseUrl), url, out var resolved))
            return resolved.AbsoluteUri;

        return url;
    }

    private static string FallbackExtract(string html)
    {
        html = RemoveNoiseTags(html);
        return HtmlToReadableText(html);
    }

    #endregion

    #region Generated Regex

    [GeneratedRegex(@"<li\s+class\s*=\s*""b_algo""[^>]*>[\s\S]*?</li>", RegexOptions.IgnoreCase)]
    private static partial Regex BingResultRegex();

    [GeneratedRegex(@"<a[^>]*href\s*=\s*""([^""]*)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"<(?:p|span)[^>]*class\s*=\s*""[^""]*(?:snippet|caption|desc)[^""]*""[^>]*>([\s\S]*?)</(?:p|span)>", RegexOptions.IgnoreCase)]
    private static partial Regex SnippetRegex();

    [GeneratedRegex(@"<img\s[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex ImgRegex();

    [GeneratedRegex(@"src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex SrcRegex();

    [GeneratedRegex(@"alt\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex AltRegex();

    [GeneratedRegex(@"<video[^>]*>[\s\S]*?</video>", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTagRegex();

    [GeneratedRegex(@"<source[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"<iframe[^>]+>[\s\S]*?</iframe>", RegexOptions.IgnoreCase)]
    private static partial Regex IframeRegex();

    [GeneratedRegex(@"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    #endregion
}

/// <summary>
/// 网页结构化内容
/// </summary>
internal sealed class PageContent
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string Body { get; set; } = string.Empty;
    public List<ImageInfo> Images { get; } = [];
    public List<VideoInfo> Videos { get; } = [];

    /// <summary>
    /// 格式化为 AI 可读的结构化文本
    /// </summary>
    public string Format(int maxLength = 20_000)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(Title))
            sb.AppendLine($"# {Title}");

        if (!string.IsNullOrWhiteSpace(Description))
            sb.AppendLine($"> {Description}").AppendLine();

        sb.AppendLine("## 具体内容");
        sb.AppendLine(Body);
        sb.AppendLine();

        if (Images.Count > 0)
        {
            sb.AppendLine("## 页面附带的配图 (请结合上面正文内容，将相关配图嵌入到你的回答中)");
            foreach (var img in Images)
            {
                sb.Append($"- ![{img.Alt ?? "图片"}]({img.Url})");
                if (!string.IsNullOrWhiteSpace(img.Alt))
                    sb.Append($" — {img.Alt}");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (Videos.Count > 0)
        {
            sb.AppendLine("## 视频");
            foreach (var video in Videos)
            {
                sb.AppendLine($"- [{video.Platform}] {video.Url}");
            }
            sb.AppendLine();
        }

        var result = sb.ToString();
        if (result.Length > maxLength)
            result = string.Concat(result.AsSpan(0, maxLength), "\n\n... (内容已截断)");

        return result;
    }
}

internal sealed record ImageInfo(string? Alt, string Url);
internal sealed record VideoInfo(string Platform, string Url);
