using System.Buffers.Binary;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools.AIImage;

/// <summary>
/// 角色一致性控制工具 — 集成 Stable Diffusion WebUI/ComfyUI API，
/// 支持 ControlNet 姿态控制、LoRA 模型加载、角色特征锁定等功能，
/// 实现 AI 漫剧制作中的角色一致性控制。
/// </summary>
[Tool("character_consistency", "角色一致性控制工具，支持 ControlNet 姿态控制、LoRA 模型加载、角色特征锁定，实现 AI 漫剧角色在不同场景中的视觉一致性",
    SecurityLevel = ToolSecurityLevel.Medium,
    TimeoutSeconds = 120)]
public sealed class CharacterConsistencyTool : IOwnerAITool
{
    private readonly IHttpClientFactory? _httpClientFactory;

    public CharacterConsistencyTool(IHttpClientFactory? httpClientFactory = null)
    {
        _httpClientFactory = httpClientFactory;
    }

    public bool IsAvailable(ToolContext context) => context.IsOwner;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        try
        {
            // 解析操作类型
            if (!parameters.TryGetProperty("operation", out var operationEl))
                return ToolResult.Error("缺少参数：operation（支持：generate、controlnet、lora_info、check_consistency）");

            var operation = operationEl.GetString();
            if (string.IsNullOrWhiteSpace(operation))
                return ToolResult.Error("operation 参数不能为空");

            return operation.ToLowerInvariant() switch
            {
                "generate" => await GenerateImageAsync(parameters, ct),
                "controlnet" => await ControlNetGenerateAsync(parameters, ct),
                "lora_info" => await GetLoRAInfoAsync(parameters, ct),
                "check_consistency" => await CheckConsistencyAsync(parameters, ct),
                _ => ToolResult.Error($"不支持的操作类型：{operation}。支持的操作：generate, controlnet, lora_info, check_consistency")
            };
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("操作超时");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"执行失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 生成图像（支持 LoRA 角色一致性）
    /// </summary>
    private async ValueTask<ToolResult> GenerateImageAsync(JsonElement parameters, CancellationToken ct)
    {
        // 解析参数
        if (!parameters.TryGetProperty("prompt", out var promptEl))
            return ToolResult.Error("缺少参数：prompt（提示词）");

        var prompt = promptEl.GetString();
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Error("提示词不能为空");

        var negativePrompt = parameters.TryGetProperty("negative_prompt", out var npEl) ? npEl.GetString() : "";
        var width = parameters.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 512;
        var height = parameters.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 512;
        var steps = parameters.TryGetProperty("steps", out var sEl) ? sEl.GetInt32() : 20;
        var cfgScale = parameters.TryGetProperty("cfg_scale", out var cEl) ? cEl.GetDouble() : 7.0;
        var seed = parameters.TryGetProperty("seed", out var seedEl) ? seedEl.GetInt64() : -1;
        var loraModels = parameters.TryGetProperty("lora_models", out var loraEl) ? loraEl.EnumerateArray().Select(e => e.GetString()!).ToList() : null;
        var loraWeights = parameters.TryGetProperty("lora_weights", out var lwEl) ? lwEl.EnumerateArray().Select(e => e.GetDouble()).ToList() : null;
        var apiEndpoint = parameters.TryGetProperty("api_endpoint", out var apiEl) ? apiEl.GetString() ?? "http://127.0.0.1:7860" : "http://127.0.0.1:7860";

        // 构建提示词（添加 LoRA）
        var finalPrompt = new StringBuilder(prompt);
        if (loraModels != null && loraModels.Count > 0)
        {
            for (int i = 0; i < loraModels.Count; i++)
            {
                var weight = loraWeights != null && i < loraWeights.Count ? loraWeights[i] : 1.0;
                finalPrompt.Append($", <lora:{loraModels[i]}:{weight:F2}>");
            }
        }

        // 构建请求体
        var requestBody = new
        {
            prompt = finalPrompt.ToString(),
            negative_prompt = negativePrompt,
            steps = steps,
            width = width,
            height = height,
            cfg_scale = cfgScale,
            seed = seed,
            sampler_name = "DPM++ 2M Karras",
            batch_size = 1
        };

        var result = await SendSDRequestAsync(apiEndpoint, "/sdapi/v1/txt2img", requestBody, ct);
        if (!result.Success)
            return result;

        // 解析结果
        if (result.Metadata != null && result.Metadata.TryGetValue("images", out var imagesObj))
        {
            var images = imagesObj as JsonElement?;
            if (images.HasValue && images.Value.ValueKind == JsonValueKind.Array)
            {
                var imageArray = images.Value.EnumerateArray().ToList();
                if (imageArray.Count > 0)
                {
                    var base64Image = imageArray[0].GetString();
                    if (!string.IsNullOrEmpty(base64Image))
                    {
                        // 返回图片数据 URL
                        var dataUrl = $"data:image/png;base64,{base64Image}";
                        return ToolResult.Ok(
                            $"图像生成成功\n\n提示词：{finalPrompt}\n尺寸：{width}x{height}\n步数：{steps}",
                            new Dictionary<string, object>
                            {
                                ["images"] = new[] { dataUrl },
                                ["seed"] = seed,
                                ["width"] = width,
                                ["height"] = height
                            });
                    }
                }
            }
        }

        return ToolResult.Error("生成成功但未返回图像数据");
    }

    /// <summary>
    /// ControlNet 姿态控制生成
    /// </summary>
    private async ValueTask<ToolResult> ControlNetGenerateAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("prompt", out var promptEl))
            return ToolResult.Error("缺少参数：prompt");

        if (!parameters.TryGetProperty("controlnet_image", out var cnImgEl))
            return ToolResult.Error("缺少参数：controlnet_image（ControlNet 输入图片的 base64 或路径）");

        var prompt = promptEl.GetString();
        var controlnetImage = cnImgEl.GetString();
        if (string.IsNullOrWhiteSpace(controlnetImage))
            return ToolResult.Error("controlnet_image 不能为空");

        var controlnetModule = parameters.TryGetProperty("controlnet_module", out var cmEl) ? cmEl.GetString() : "openpose";
        var controlnetModel = parameters.TryGetProperty("controlnet_model", out var cmmEl) ? cmmEl.GetString() : "control_v11p_sd15_openpose";
        var controlnetWeight = parameters.TryGetProperty("controlnet_weight", out var cwEl) ? cwEl.GetDouble() : 1.0;
        var negativePrompt = parameters.TryGetProperty("negative_prompt", out var npEl) ? npEl.GetString() : "";
        var width = parameters.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 512;
        var height = parameters.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 512;
        var steps = parameters.TryGetProperty("steps", out var sEl) ? sEl.GetInt32() : 20;
        var cfgScale = parameters.TryGetProperty("cfg_scale", out var cEl) ? cEl.GetDouble() : 7.0;
        var apiEndpoint = parameters.TryGetProperty("api_endpoint", out var apiEl) ? apiEl.GetString() ?? "http://127.0.0.1:7860" : "http://127.0.0.1:7860";

        // 如果提供的是文件路径，读取并转换为 base64
        var imageBase64 = controlnetImage;
        if (!controlnetImage.StartsWith("data:", StringComparison.Ordinal) && !controlnetImage.StartsWith("/9j/", StringComparison.Ordinal) && System.IO.File.Exists(controlnetImage))
        {
            var fileBytes = await System.IO.File.ReadAllBytesAsync(controlnetImage, ct);
            imageBase64 = Convert.ToBase64String(fileBytes);
        }

        // 构建 ControlNet 请求
        var requestBody = new
        {
            prompt = prompt,
            negative_prompt = negativePrompt,
            steps = steps,
            width = width,
            height = height,
            cfg_scale = cfgScale,
            sampler_name = "DPM++ 2M Karras",
            alwayson_scripts = new
            {
                controlnet = new
                {
                    args = new object[]
                    {
                        new
                        {
                            enabled = true,
                            module = controlnetModule,
                            model = controlnetModel,
                            weight = controlnetWeight,
                            image = imageBase64,
                            resize_mode = 1, // Just resize
                            processor_res = 512,
                            threshold_a = 64,
                            threshold_b = 64,
                            guidance_start = 0.0,
                            guidance_end = 1.0,
                            pixel_perfect = true,
                            control_mode = 0 // Balanced
                        }
                    }
                }
            }
        };

        var result = await SendSDRequestAsync(apiEndpoint, "/sdapi/v1/txt2img", requestBody, ct);
        if (!result.Success)
            return result;

        return result;
    }

    /// <summary>
    /// 获取 LoRA 模型信息
    /// </summary>
    private async ValueTask<ToolResult> GetLoRAInfoAsync(JsonElement parameters, CancellationToken ct)
    {
        var apiEndpoint = parameters.TryGetProperty("api_endpoint", out var apiEl) ? apiEl.GetString() ?? "http://127.0.0.1:7860" : "http://127.0.0.1:7860";
        var loraPath = parameters.TryGetProperty("lora_path", out var lpEl) ? lpEl.GetString() : null;

        try
        {
            var client = CreateHttpClient(apiEndpoint);
            var url = loraPath != null 
                ? $"{apiEndpoint}/sdapi/v1/loras?lora_path={Uri.EscapeDataString(loraPath)}"
                : $"{apiEndpoint}/sdapi/v1/loras";

            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var loras = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var loraList = new List<string>();

            if (loras.ValueKind == JsonValueKind.Array)
            {
                foreach (var lora in loras.EnumerateArray())
                {
                    if (lora.TryGetProperty("name", out var nameEl))
                    {
                        loraList.Add(nameEl.GetString()!);
                    }
                }
            }

            var resultText = new StringBuilder($"找到 {loraList.Count} 个 LoRA 模型:\n\n");
            foreach (var lora in loraList.Take(20))
            {
                resultText.AppendLine($"- {lora}");
            }
            if (loraList.Count > 20)
            {
                resultText.AppendLine($"\n... 还有 {loraList.Count - 20} 个模型");
            }

            return ToolResult.Ok(resultText.ToString(), new Dictionary<string, object>
            {
                ["lora_count"] = loraList.Count,
                ["loras"] = loraList.ToArray()
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"获取 LoRA 列表失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 检查角色一致性（比较两张图片的特征相似度）
    /// </summary>
    private static async ValueTask<ToolResult> CheckConsistencyAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!parameters.TryGetProperty("image1", out var img1El))
            return ToolResult.Error("缺少参数：image1（参考图片）");

        if (!parameters.TryGetProperty("image2", out var img2El))
            return ToolResult.Error("缺少参数：image2（待检查图片）");

        var image1 = img1El.GetString();
        var image2 = img2El.GetString();
        if (string.IsNullOrWhiteSpace(image1))
            return ToolResult.Error("image1 不能为空");
        if (string.IsNullOrWhiteSpace(image2))
            return ToolResult.Error("image2 不能为空");

        var apiEndpoint = parameters.TryGetProperty("api_endpoint", out var apiEl) ? apiEl.GetString() ?? "http://127.0.0.1:7860" : "http://127.0.0.1:7860";
        _ = apiEndpoint; // reserved for future similarity API calls

        // 读取图片并转换为 base64
        string base64Img1, base64Img2;
        try
        {
            if (image1.StartsWith("data:", StringComparison.Ordinal))
            {
                base64Img1 = image1.Split(',')[1];
            }
            else if (System.IO.File.Exists(image1))
            {
                base64Img1 = Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(image1, ct));
            }
            else
            {
                return ToolResult.Error($"图片 1 不存在：{image1}");
            }

            if (image2.StartsWith("data:", StringComparison.Ordinal))
            {
                base64Img2 = image2.Split(',')[1];
            }
            else if (System.IO.File.Exists(image2))
            {
                base64Img2 = Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(image2, ct));
            }
            else
            {
                return ToolResult.Error($"图片 2 不存在：{image2}");
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"读取图片失败：{ex.Message}");
        }

        // 使用 REINSW 或类似扩展进行相似度分析（如果可用）
        // 这里提供一个基础的实现框架
        var resultText = new StringBuilder("角色一致性检查:\n\n");
        resultText.AppendLine("⚠️ 注意：完整的角色一致性检查需要安装 REINSW 或 IP-Adapter 扩展");
        resultText.AppendLine("\n当前提供基础检查：");
        resultText.AppendLine($"- 图片 1 尺寸：{await GetImageDimensionsAsync(base64Img1)}");
        resultText.AppendLine($"- 图片 2 尺寸：{await GetImageDimensionsAsync(base64Img2)}");
        resultText.AppendLine("\n建议：使用 IP-Adapter FaceID 或 ReActor 扩展进行面部相似度分析");

        return ToolResult.Ok(resultText.ToString());
    }

    /// <summary>
    /// 发送请求到 Stable Diffusion WebUI
    /// </summary>
    private async ValueTask<ToolResult> SendSDRequestAsync(string apiEndpoint, string endpoint, object requestBody, CancellationToken ct)
    {
        try
        {
            var client = CreateHttpClient(apiEndpoint);
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(endpoint, content, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return ToolResult.Error($"API 请求失败 ({response.StatusCode}): {responseJson}");
            }

            var resultElement = JsonDocument.Parse(responseJson).RootElement;
            var metadata = new Dictionary<string, object>();

            if (resultElement.TryGetProperty("images", out var imagesEl))
            {
                metadata["images"] = imagesEl;
            }
            if (resultElement.TryGetProperty("parameters", out var paramsEl))
            {
                metadata["parameters"] = paramsEl;
            }
            if (resultElement.TryGetProperty("info", out var infoEl))
            {
                metadata["info"] = infoEl.ToString();
            }

            return ToolResult.Ok("图像生成成功", metadata);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"HTTP 请求失败：{ex.Message}\n\n请确保 Stable Diffusion WebUI 正在运行，并且 API 端点地址正确。\n默认地址：http://127.0.0.1:7860");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"请求失败：{ex.Message}");
        }
    }

    private HttpClient CreateHttpClient(string baseUrl)
    {
        var client = _httpClientFactory?.CreateClient() ?? new HttpClient();
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.Timeout = TimeSpan.FromSeconds(120);
        return client;
    }

    private static ValueTask<string> GetImageDimensionsAsync(string base64Image)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64Image);

            // PNG: 89 50 4E 47 header, width at offset 16, height at offset 20 (big-endian uint32)
            if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                var w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                var h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
                return ValueTask.FromResult($"{w}x{h}");
            }

            // JPEG: FF D8 header, find SOF0 (FF C0) or SOF2 (FF C2) marker
            if (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                for (var i = 2; i < bytes.Length - 9; i++)
                {
                    if (bytes[i] == 0xFF && (bytes[i + 1] is 0xC0 or 0xC2))
                    {
                        var h = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i + 5, 2));
                        var w = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i + 7, 2));
                        return ValueTask.FromResult($"{w}x{h}");
                    }
                }
            }

            return ValueTask.FromResult("无法解析");
        }
        catch
        {
            return ValueTask.FromResult("无法解析");
        }
    }
}
