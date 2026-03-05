using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class DelegateToModelTool : IToolHandler
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, LLMConfig> _subModels;

    public string Name => "delegate_to_model";

    public DelegateToModelTool(HttpClient http, Dictionary<string, LLMConfig> subModels)
    {
        _http = http;
        _subModels = subModels;
    }

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "将任务分发给专业的次级 AI 模型处理。vision 用于图像分析，coding 用于代码生成/调试，science 用于数学/科学计算，multimodal 用于多模态任务",
            Parameters = new()
            {
                Properties = new()
                {
                    ["Category"] = new()
                    {
                        Type = "string",
                        Description = "目标模型类别",
                        Enum = ["vision", "coding", "science", "multimodal"]
                    },
                    ["task"] = new()
                    {
                        Type = "string",
                        Description = "要委托给次级模型完成的任务描述"
                    },
                    ["system_instruction"] = new()
                    {
                        Type = "string",
                        Description = "给次级模型的系统提示词（可选，用于调整模型行为）"
                    }
                },
                Required = ["Category", "task"]
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        if (!parameters.TryGetValue("Category", out var categoryObj) || categoryObj is null)
            return "错误：缺少 Category 参数";
        if (!parameters.TryGetValue("task", out var taskObj) || taskObj is null)
            return "错误：缺少 task 参数";

        var category = categoryObj.ToString()!.ToLower();
        var task = taskObj.ToString()!;
        var systemInstruction = parameters.TryGetValue("system_instruction", out var sysObj) && sysObj is not null
            ? sysObj.ToString()!
            : "";

        if (!_subModels.TryGetValue(category, out var modelConfig))
            return $"错误：未配置 {category} 类别的模型，请在 appsettings.json 的 SubModels 中配置";

        if (string.IsNullOrWhiteSpace(modelConfig.ApiKey))
            return $"错误：{category} 模型未配置 ApiKey，请在 appsettings.json 中填写";

        try
        {
            var messages = new List<ChatMessage>();

            if (!string.IsNullOrWhiteSpace(systemInstruction))
                messages.Add(new ChatMessage { Role = "system", Content = systemInstruction });
            else
                messages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = GetDefaultSystemInstruction(category)
                });

            messages.Add(new ChatMessage { Role = "user", Content = task });

            var chatRequest = new ChatRequest
            {
                Model = modelConfig.Model,
                Messages = messages,
                MaxTokens = modelConfig.MaxTokens
            };

            var json = JsonSerializer.Serialize(chatRequest);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{modelConfig.BaseUrl.TrimEnd('/')}/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", modelConfig.ApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return $"模型 {category} 请求失败（HTTP {(int)response.StatusCode}）：{responseJson}";

            var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseJson);
            var content = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content?.ToString();

            if (string.IsNullOrWhiteSpace(content))
                return $"模型 {category} 未返回有效响应";

            return $"[{category.ToUpper()} 模型响应]\n{content}";
        }
        catch (Exception ex)
        {
            return $"委托给 {category} 模型失败：{ex.Message}";
        }
    }

    private static string GetDefaultSystemInstruction(string category) => category switch
    {
        "vision" => "你是一个专业的图像分析助手，擅长理解和描述图像内容。",
        "coding" => "你是一个专业的代码助手，擅长代码生成、调试和优化。提供清晰、高质量的代码实现。",
        "science" => "你是一个专业的科学与数学助手，擅长数学推导、科学计算和公式推导。",
        "multimodal" => "你是一个多模态助手，能够处理文本、图像等多种形式的内容。",
        _ => "你是一个专业的 AI 助手，尽力完成用户交代的任务。"
    };
}
