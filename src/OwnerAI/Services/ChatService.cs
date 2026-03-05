using OwnerAI.Models;

namespace OwnerAI.Services;

public class ChatService
{
    private readonly LLMService _llm;
    private readonly ToolRegistry _tools;
    private readonly List<ChatMessage> _history = new();
    private const int MaxToolCallRounds = 10;

    public ChatService(LLMService llm, ToolRegistry tools)
    {
        _llm = llm;
        _tools = tools;
    }

    public void SetSystemPrompt(string systemPrompt)
    {
        _history.Clear();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            _history.Add(new ChatMessage { Role = "system", Content = systemPrompt });
    }

    public void ClearHistory()
    {
        var systemMsg = _history.FirstOrDefault(m => m.Role == "system");
        _history.Clear();
        if (systemMsg != null)
            _history.Add(systemMsg);
    }

    public async Task<string> SendAsync(
        string userMessage,
        Action<string>? onToolCall = null,
        CancellationToken cancellationToken = default)
    {
        _history.Add(new ChatMessage { Role = "user", Content = userMessage });

        var toolDefs = _tools.GetAllDefinitions();
        var round = 0;

        while (round < MaxToolCallRounds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _llm.ChatAsync(_history, toolDefs, cancellationToken);

            if (response.Error != null)
                return $"❌ 错误：{response.Error.Message}";

            var choice = response.Choices?.FirstOrDefault();
            if (choice == null)
                return "❌ 错误：API 未返回任何响应";

            var message = choice.Message;
            if (message == null)
                return "❌ 错误：响应消息为空";

            // Add assistant message to history
            _history.Add(message);

            // Check if there are tool calls to execute
            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    var toolName = toolCall.Function.Name;
                    var args = toolCall.Function.Arguments;

                    onToolCall?.Invoke($"🔧 调用工具：{toolName}（参数：{TruncateArgs(args)}）");

                    var result = await _tools.ExecuteAsync(toolName, args);

                    onToolCall?.Invoke($"✅ 工具结果：{TruncateResult(result)}");

                    _history.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Name = toolName,
                        Content = result
                    });
                }

                round++;
                continue;
            }

            // No tool calls - return the final text response
            var content = message.Content?.ToString() ?? "";
            return content;
        }

        return "❌ 错误：工具调用轮次超过限制，停止处理";
    }

    private static string TruncateArgs(string args)
    {
        return args.Length > 100 ? args[..100] + "..." : args;
    }

    private static string TruncateResult(string result)
    {
        var firstLine = result.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return firstLine.Length > 80 ? firstLine[..80] + "..." : firstLine;
    }
}
