using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OwnerAI.Agent.Providers;
using OwnerAI.Configuration;
using OwnerAI.Shared;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Orchestration;

/// <summary>
/// 多模型协作工具 — 主模型通过此工具将任务分发给次级专业模型
/// </summary>
[Tool("delegate_to_model", "将任务分发给指定类别的专业 AI 模型执行（如代码模型生成代码、推理模型做复杂推理、视觉模型识别图片、文生图模型画图、图生视频模型从图片生成视频、文生视频模型从文字生成视频、翻译模型做翻译等）",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 600)]
public sealed class ModelRouterTool(
    ProviderRegistry registry,
    ILogger<ModelRouterTool> logger) : IOwnerAITool
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly HttpClient s_httpClient = new();

    public bool IsAvailable(ToolContext context) => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        // 解析目标类别
        if (!parameters.TryGetProperty("category", out var catEl))
            return ToolResult.Error("缺少参数: category (可选值: llm, coding, reasoning, vision, imagegen, i2v, t2v, audio, translation, writing, dataanalysis, embedding, science, multimodal)");

        var categoryStr = catEl.GetString()?.ToLowerInvariant();
        if (!TryParseCategory(categoryStr, out var category))
            return ToolResult.Error($"未知的模型类别: {categoryStr}。可选: llm, coding, reasoning, vision, imagegen, i2v(图生视频), t2v(文生视频), audio, translation, writing, dataanalysis, embedding, science, multimodal");

        // 解析任务描述
        if (!parameters.TryGetProperty("task", out var taskEl))
            return ToolResult.Error("缺少参数: task (任务描述)");

        var task = taskEl.GetString();
        if (string.IsNullOrWhiteSpace(task))
            return ToolResult.Error("任务描述不能为空");

        // 可选的系统指令
        var systemInstruction = parameters.TryGetProperty("system_instruction", out var sysEl)
            ? sysEl.GetString()
            : null;

        // 可选的显式图片路径列表（主模型可通过此参数指定特定图片）
        List<string>? imageUrls = null;
        if (parameters.TryGetProperty("image_urls", out var imgEl) && imgEl.ValueKind == JsonValueKind.Array)
        {
            imageUrls = [];
            foreach (var item in imgEl.EnumerateArray())
            {
                var url = item.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    imageUrls.Add(url);
            }
        }

        // 查找目标模型
        var provider = registry.GetByCategory(category);
        if (provider is null)
        {
            var available = registry.GetAvailableCategories();
            var availableStr = string.Join(", ", available.Select(c => c.ToString().ToLowerInvariant()));
            return ToolResult.Error($"未配置 {category} 类别的模型。当前已配置的类别: {availableStr}");
        }

        logger.LogInformation("[ModelRouter] Dispatching to {Category} model '{Name}': {Task}",
            category, provider.Name, task.Length > 100 ? task[..100] + "..." : task);

        try
        {
            // 视频生成类别 — 使用 DashScope 原生异步 API（提交任务 → 轮询结果）
            if (IsVideoGenCategory(category) && IsDashScopeProvider(provider.Endpoint))
            {
                return await CallDashScopeVideoAsync(provider, category, task!, imageUrls, context.Attachments, ct);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(110));

            var messages = new List<ChatMessage>();

            if (!string.IsNullOrWhiteSpace(systemInstruction))
                messages.Add(new ChatMessage(ChatRole.System, systemInstruction));

            // 构建用户消息 — 根据类别决定是否附带媒体内容
            messages.Add(BuildDelegationMessage(task, category, imageUrls, context.Attachments));

            var response = await provider.Client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var resultText = response.Text ?? "(无回复)";

            logger.LogInformation("[ModelRouter] {Category} model '{Name}' responded ({Length} chars)",
                category, provider.Name, resultText.Length);

            var sb = new StringBuilder();
            sb.Append("[模型: ").Append(provider.Name).Append(" (").Append(category).AppendLine(")]");
            sb.AppendLine(resultText);

            return ToolResult.Ok(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"{category} 模型 '{provider.Name}' 调用超时");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ModelRouter] {Category} model '{Name}' failed", category, provider.Name);
            return ToolResult.Error($"{category} 模型调用失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 判断模型类别是否需要媒体内容（图片/视频）
    /// </summary>
    private static bool CategoryNeedsMedia(ModelCategory category) => category is
        ModelCategory.Vision or ModelCategory.Multimodal or
        ModelCategory.ImageToVideo or ModelCategory.ImageGen;

    /// <summary>
    /// 构建分发给次级模型的用户消息 — 需要媒体的类别会附带图片/视频数据
    /// </summary>
    private ChatMessage BuildDelegationMessage(
        string task,
        ModelCategory category,
        List<string>? explicitImageUrls,
        IReadOnlyList<MediaAttachment>? contextAttachments)
    {
        var needsMedia = CategoryNeedsMedia(category);
        var hasExplicitImages = explicitImageUrls is { Count: > 0 };
        var hasContextAttachments = contextAttachments is { Count: > 0 };

        // 无需媒体或无可用媒体 → 纯文本消息
        if (!needsMedia && !hasExplicitImages)
            return new ChatMessage(ChatRole.User, task);

        if (!hasExplicitImages && !hasContextAttachments)
            return new ChatMessage(ChatRole.User, task);

        var contents = new List<AIContent> { new TextContent(task) };
        var loadedCount = 0;

        // 优先使用显式指定的图片 URL/路径
        if (hasExplicitImages)
        {
            foreach (var url in explicitImageUrls!)
            {
                var content = LoadMediaFromUrl(url);
                if (content is not null)
                {
                    contents.Add(content);
                    loadedCount++;
                }
            }
        }

        // 如果没有显式图片，则从对话上下文附件中加载
        if (loadedCount == 0 && hasContextAttachments)
        {
            foreach (var att in contextAttachments!)
            {
                var content = AgentExecutor.LoadAttachmentContent(att);
                if (content is not null)
                {
                    contents.Add(content);
                    loadedCount++;
                }
            }
        }

        if (loadedCount > 0)
        {
            logger.LogInformation("[ModelRouter] Attached {Count} media item(s) to {Category} delegation",
                loadedCount, category);
        }

        return contents.Count > 1
            ? new ChatMessage(ChatRole.User, contents)
            : new ChatMessage(ChatRole.User, task);
    }

    /// <summary>
    /// 从 URL 或本地路径加载媒体内容为 DataContent
    /// </summary>
    private static DataContent? LoadMediaFromUrl(string url)
    {
        // 本地文件
        if (File.Exists(url))
        {
            var bytes = File.ReadAllBytes(url);
            var mediaType = GuessMediaType(url);
            return new DataContent(bytes, mediaType);
        }

        // 远程 URL
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var mediaType = GuessMediaType(url);
            return new DataContent(uri, mediaType);
        }

        return null;
    }

    /// <summary>
    /// 根据文件扩展名猜测 MIME 类型
    /// </summary>
    private static string GuessMediaType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream",
        };
    }

    /// <summary>判断是否为视频生成类别</summary>
    private static bool IsVideoGenCategory(ModelCategory category) => category is
        ModelCategory.ImageToVideo or ModelCategory.TextToVideo;

    /// <summary>判断供应商端点是否为阿里云 DashScope</summary>
    private static bool IsDashScopeProvider(string? endpoint) =>
        endpoint?.Contains("dashscope.aliyuncs.com", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 将图片源解析为 DashScope 可用的格式。
    /// DashScope img_url 仅支持公网 HTTP(S) URL 和 base64 data URI，不支持本地路径。
    /// 本地文件或附件字节数据会被自动转为 data URI。
    /// </summary>
    private static string? ResolveImageForDashScope(string? explicitUrl, IReadOnlyList<MediaAttachment>? attachments)
    {
        // 1. 尝试显式 URL/路径
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (explicitUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || explicitUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return explicitUrl;

            if (explicitUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return explicitUrl;

            // 本地文件 → 读取并转为 base64 data URI
            if (File.Exists(explicitUrl))
            {
                var bytes = File.ReadAllBytes(explicitUrl);
                var mime = GuessMediaType(explicitUrl);
                return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }
        }

        // 2. 从对话附件中获取第一张图片
        if (attachments is not { Count: > 0 })
            return null;

        var imgAtt = attachments.FirstOrDefault(
            a => a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

        if (imgAtt is null)
            return null;

        // 附件有内嵌字节数据
        if (imgAtt.Data is { Length: > 0 })
            return $"data:{imgAtt.ContentType};base64,{Convert.ToBase64String(imgAtt.Data)}";

        // 附件有 URL
        if (string.IsNullOrWhiteSpace(imgAtt.Url))
            return null;

        if (imgAtt.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || imgAtt.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return imgAtt.Url;

        // 本地路径 → base64
        if (File.Exists(imgAtt.Url))
        {
            var bytes = File.ReadAllBytes(imgAtt.Url);
            return $"data:{imgAtt.ContentType};base64,{Convert.ToBase64String(bytes)}";
        }

        return null;
    }

    /// <summary>
    /// 使用 DashScope 原生异步 API 生成视频
    /// 流程: 提交任务 → 每 5 秒轮询状态 → 返回视频 URL
    /// </summary>
    private async Task<ToolResult> CallDashScopeVideoAsync(
        ProviderEntry provider,
        ModelCategory category,
        string prompt,
        List<string>? imageUrls,
        IReadOnlyList<MediaAttachment>? attachments,
        CancellationToken ct)
    {
        var baseUrl = new Uri(provider.Endpoint!).GetLeftPart(UriPartial.Authority);
        var apiKey = provider.ApiKey;
        var modelId = provider.ModelId
            ?? (category == ModelCategory.ImageToVideo ? "wan2.6-i2v" : "wan2.6-t2v");

        // 构建 input
        var input = new Dictionary<string, object> { ["prompt"] = prompt };

        // 图生视频需要 img_url（必须是公网 URL 或 base64 data URI，本地路径不行）
        if (category == ModelCategory.ImageToVideo)
        {
            var resolvedImg = ResolveImageForDashScope(
                imageUrls?.FirstOrDefault(), attachments);

            if (!string.IsNullOrWhiteSpace(resolvedImg))
                input["img_url"] = resolvedImg;
            else
                logger.LogWarning("[ModelRouter] ImageToVideo requested but no valid image source found");
        }

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = modelId,
            ["input"] = input,
            ["parameters"] = new Dictionary<string, object>
            {
                ["resolution"] = "720P",
                ["prompt_extend"] = true,
            },
        };

        // ── 提交异步任务 ──
        var submitUrl = $"{baseUrl}/api/v1/services/aigc/video-generation/video-synthesis";
        using var submitReq = new HttpRequestMessage(HttpMethod.Post, submitUrl);
        submitReq.Headers.Add("Authorization", $"Bearer {apiKey}");
        submitReq.Headers.Add("X-DashScope-Async", "enable");
        submitReq.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, s_jsonOptions),
            Encoding.UTF8,
            "application/json");

        var submitResp = await s_httpClient.SendAsync(submitReq, ct);
        var submitJson = await submitResp.Content.ReadAsStringAsync(ct);

        if (!submitResp.IsSuccessStatusCode)
            return ToolResult.Error($"DashScope 视频生成提交失败: HTTP {(int)submitResp.StatusCode} — {submitJson}");

        using var submitDoc = JsonDocument.Parse(submitJson);
        if (!submitDoc.RootElement.TryGetProperty("output", out var submitOutput)
            || !submitOutput.TryGetProperty("task_id", out var taskIdEl))
            return ToolResult.Error($"DashScope 视频生成失败: 未获取到 task_id — {submitJson}");

        var taskId = taskIdEl.GetString();
        logger.LogInformation("[ModelRouter] DashScope video task submitted: {TaskId} (model={Model})", taskId, modelId);

        // ── 轮询任务状态（最长 5 分钟，每 5 秒一次） ──
        var pollUrl = $"{baseUrl}/api/v1/tasks/{taskId}";
        const int maxPolls = 60;

        for (var i = 0; i < maxPolls; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
            pollReq.Headers.Add("Authorization", $"Bearer {apiKey}");

            var pollResp = await s_httpClient.SendAsync(pollReq, ct);
            var pollJson = await pollResp.Content.ReadAsStringAsync(ct);

            using var pollDoc = JsonDocument.Parse(pollJson);
            if (!pollDoc.RootElement.TryGetProperty("output", out var output))
                continue;

            var status = output.TryGetProperty("task_status", out var statusEl)
                ? statusEl.GetString()
                : null;

            logger.LogDebug("[ModelRouter] DashScope task {TaskId} status: {Status}", taskId, status);

            if (status == "SUCCEEDED")
            {
                // 尝试多种响应格式提取视频 URL
                string? videoUrl = null;
                if (output.TryGetProperty("video_url", out var vuEl))
                    videoUrl = vuEl.GetString();

                if (videoUrl is null && output.TryGetProperty("results", out var resultsEl)
                    && resultsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in resultsEl.EnumerateArray())
                    {
                        if (r.TryGetProperty("url", out var urlEl))
                        {
                            videoUrl = urlEl.GetString();
                            if (videoUrl is not null) break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(videoUrl))
                    return ToolResult.Error("视频生成成功但未返回视频 URL");

                logger.LogInformation("[ModelRouter] DashScope video ready: {Url}", videoUrl);

                var sb = new StringBuilder();
                sb.Append("[模型: ").Append(provider.Name).Append(" (").Append(category).AppendLine(")]");
                sb.AppendLine("✅ 视频生成成功！");
                sb.Append("视频链接: ").AppendLine(videoUrl);

                return new ToolResult
                {
                    Success = true,
                    Output = sb.ToString(),
                    MediaUrls = [new ToolMediaUrl(videoUrl, ToolMediaKind.Video,
                        prompt.Length > 100 ? prompt[..100] : prompt)],
                };
            }

            if (status is "FAILED" or "CANCELED")
            {
                var errMsg = output.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                if (errMsg is null && output.TryGetProperty("code", out var codeEl))
                    errMsg = codeEl.GetString();
                return ToolResult.Error($"DashScope 视频生成失败: {errMsg ?? "未知错误"}");
            }

            // PENDING / RUNNING → 继续轮询
        }

        return ToolResult.Error($"DashScope 视频生成超时（已等待 {maxPolls * 5} 秒），task_id={taskId}");
    }

    private static bool TryParseCategory(string? value, out ModelCategory category)
    {
        category = value switch
        {
            "llm" or "language" or "text" or "chat" => ModelCategory.LLM,
            "vision" or "visual" or "image_understanding" or "ocr"
                or "image_recognition" or "recognition" or "detect" or "classify"
                or "图像识别" => ModelCategory.Vision,
            "multimodal" or "multi" => ModelCategory.Multimodal,
            "science" or "scientific" => ModelCategory.Science,
            "coding" or "code" or "coder" or "programming" => ModelCategory.Coding,
            "reasoning" or "reason" or "logic" or "math" or "think" => ModelCategory.Reasoning,
            "imagegen" or "image_gen" or "image_generation" or "drawing" or "art" or "paint"
                or "text_to_image" or "t2i" or "文生图" => ModelCategory.ImageGen,
            "i2v" or "img2video" or "image_to_video" or "imagetovidee"
                or "图生视频" => ModelCategory.ImageToVideo,
            "t2v" or "txt2video" or "text_to_video" or "texttovideo"
                or "文生视频" => ModelCategory.TextToVideo,
            "audio" or "speech" or "tts" or "stt" or "voice" => ModelCategory.Audio,
            "translation" or "translate" or "trans" => ModelCategory.Translation,
            "writing" or "write" or "creative" or "copywriting" => ModelCategory.Writing,
            "dataanalysis" or "data_analysis" or "data" or "analytics" or "statistics" => ModelCategory.DataAnalysis,
            "embedding" or "embed" or "vector" or "retrieval" => ModelCategory.Embedding,
            _ => default,
        };
        return value is not null && category != default || value is "llm";
    }
}
