using System.Text.Json;
using OwnerAI.Models;
using OwnerAI.Tools;

namespace OwnerAI.Services;

public class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> _tools = new();

    public void Register(IToolHandler tool)
    {
        _tools[tool.Name] = tool;
    }

    public IReadOnlyDictionary<string, IToolHandler> Tools => _tools;

    public List<ToolDefinition> GetAllDefinitions()
    {
        return _tools.Values.Select(t => t.GetDefinition()).ToList();
    }

    public async Task<string> ExecuteAsync(string toolName, string argumentsJson)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
            return $"错误：未知工具 '{toolName}'";

        Dictionary<string, object?> parameters;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            parameters = ParseParameters(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return $"错误：无法解析工具参数：{ex.Message}";
        }

        try
        {
            return await tool.ExecuteAsync(parameters);
        }
        catch (Exception ex)
        {
            return $"工具 '{toolName}' 执行失败：{ex.Message}";
        }
    }

    private static Dictionary<string, object?> ParseParameters(JsonElement element)
    {
        // Use case-insensitive dictionary so tools work regardless of LLM casing
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object) return dict;

        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                JsonValueKind.True => (object?)true,
                JsonValueKind.False => (object?)false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }
        return dict;
    }
}
