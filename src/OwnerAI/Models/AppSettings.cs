namespace OwnerAI.Models;

public class AppSettings
{
    public LLMConfig LLM { get; set; } = new();
    public Dictionary<string, LLMConfig> SubModels { get; set; } = new();
    public BingSearchConfig Bing { get; set; } = new();
}

public class LLMConfig
{
    public string BaseUrl { get; set; } = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "qwen-max";
    public int MaxTokens { get; set; } = 4096;
}

public class BingSearchConfig
{
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "https://api.bing.microsoft.com/v7.0/search";
}
