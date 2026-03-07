using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OwnerAI.Agent.Orchestration;

/// <summary>
/// 自定义 AIFunction — 使用显式 JSON Schema 定义参数，完全避免反射
/// 解决 NativeAOT/trimming 环境下 AIFunctionFactory 反射失败的问题
/// </summary>
internal sealed class ToolAIFunction : AIFunction
{
    private readonly Func<JsonElement, CancellationToken, Task<string>> _handler;

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    public ToolAIFunction(
        string name,
        string description,
        JsonElement parameterSchema,
        Func<JsonElement, CancellationToken, Task<string>> handler)
    {
        Name = name;
        Description = description;
        JsonSchema = parameterSchema;
        _handler = handler;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // 将 AIFunctionArguments 转为 JsonElement 传给工具
        JsonElement argsElement;

        if (arguments.Count == 0)
        {
            argsElement = JsonDocument.Parse("{}").RootElement;
        }
        else
        {
            // 手动构建 JSON 对象 — 不使用 JsonSerializer（避免反射）
            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var kvp in arguments)
                {
                    writer.WritePropertyName(kvp.Key);
                    WriteValue(writer, kvp.Value);
                }
                writer.WriteEndObject();
            }
            stream.Position = 0;
            using var doc = JsonDocument.Parse(stream);
            argsElement = doc.RootElement.Clone();
        }

        return await _handler(argsElement, cancellationToken);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case JsonElement je:
                je.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
