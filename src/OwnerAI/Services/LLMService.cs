using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OwnerAI.Models;

namespace OwnerAI.Services;

public class LLMService
{
    private readonly HttpClient _http;
    private readonly LLMConfig _config;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    public LLMService(HttpClient http, LLMConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<ChatResponse> ChatAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest
        {
            Model = _config.Model,
            Messages = messages,
            MaxTokens = _config.MaxTokens,
            Tools = tools?.Count > 0 ? tools : null,
            ToolChoice = tools?.Count > 0 ? "auto" : null
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var endpoint = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage? httpResponse = null;
        try
        {
            httpResponse = await _http.SendAsync(httpRequest, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ChatResponse
            {
                Error = new ApiError { Message = $"网络请求失败：{ex.Message}" }
            };
        }

        var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            string errorMsg;
            try
            {
                var errResponse = JsonSerializer.Deserialize<ChatResponse>(responseBody);
                errorMsg = errResponse?.Error?.Message ?? responseBody;
            }
            catch
            {
                errorMsg = responseBody;
            }
            return new ChatResponse
            {
                Error = new ApiError
                {
                    Message = $"API 错误（HTTP {(int)httpResponse.StatusCode}）：{errorMsg}"
                }
            };
        }

        try
        {
            var response = JsonSerializer.Deserialize<ChatResponse>(responseBody);
            return response ?? new ChatResponse
            {
                Error = new ApiError { Message = "无法解析 API 响应" }
            };
        }
        catch (JsonException ex)
        {
            return new ChatResponse
            {
                Error = new ApiError { Message = $"解析响应失败：{ex.Message}\n响应内容：{responseBody}" }
            };
        }
    }
}
