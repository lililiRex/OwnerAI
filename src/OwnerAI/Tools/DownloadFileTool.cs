using OwnerAI.Models;

namespace OwnerAI.Tools;

public class DownloadFileTool : IToolHandler
{
    private readonly HttpClient _http;

    public string Name => "download_file";

    public DownloadFileTool(HttpClient http)
    {
        _http = http;
    }

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "从 URL 下载文件（图片、文档、任意文件）并保存到本地路径",
            Parameters = new()
            {
                Properties = new()
                {
                    ["Url"] = new() { Type = "string", Description = "要下载文件的 URL 地址" },
                    ["save_path"] = new()
                    {
                        Type = "string",
                        Description = "文件保存的本地路径（包含文件名，如 C:\\Downloads\\file.pdf）"
                    }
                },
                Required = ["Url", "save_path"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("Url", out var urlObj) || urlObj is null)
            return "错误：缺少 Url 参数";
        if (!parameters.TryGetValue("save_path", out var pathObj) || pathObj is null)
            return "错误：缺少 save_path 参数";

        var url = urlObj.ToString()!;
        var savePath = pathObj.ToString()!;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return "错误：无效的 URL，仅支持 http 和 https";

        try
        {
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);

            var info = new FileInfo(savePath);
            var size = info.Length < 1024 ? $"{info.Length}B"
                : info.Length < 1024 * 1024 ? $"{info.Length / 1024}KB"
                : $"{info.Length / 1024 / 1024}MB";

            return $"文件已下载成功！\n保存路径：{savePath}\n文件大小：{size}\n内容类型：{contentType}";
        }
        catch (HttpRequestException ex)
        {
            return $"下载失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"保存文件失败：{ex.Message}";
        }
    }
}
