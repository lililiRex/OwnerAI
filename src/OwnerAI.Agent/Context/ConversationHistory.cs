using Microsoft.Extensions.AI;

namespace OwnerAI.Agent.Context;

/// <summary>
/// 对话历史管理
/// </summary>
public sealed class ConversationHistory
{
    private readonly List<ChatMessage> _messages = [];

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AddUser(string text)
        => _messages.Add(new ChatMessage(ChatRole.User, text));

    public void AddAssistant(string text)
        => _messages.Add(new ChatMessage(ChatRole.Assistant, text));

    public void AddSystem(string text)
        => _messages.Insert(0, new ChatMessage(ChatRole.System, text));

    public void AddToolResult(string toolCallId, string result)
        => _messages.Add(new ChatMessage(ChatRole.Tool, result));

    public void Clear() => _messages.Clear();

    public int Count => _messages.Count;
}
