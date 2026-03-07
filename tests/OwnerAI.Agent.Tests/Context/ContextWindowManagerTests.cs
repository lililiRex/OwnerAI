using Microsoft.Extensions.AI;
using OwnerAI.Agent.Context;

namespace OwnerAI.Agent.Tests.Context;

public class ContextWindowManagerTests
{
    [Fact]
    public void TrimHistory_EmptyList_ReturnsEmpty()
    {
        var manager = new ContextWindowManager(128_000);
        var result = manager.TrimHistory([], "system prompt");

        Assert.Empty(result);
    }

    [Fact]
    public void TrimHistory_SmallHistory_ReturnsAll()
    {
        var manager = new ContextWindowManager(128_000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi"),
        };

        var result = manager.TrimHistory(messages, "system prompt");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TrimHistory_LargeHistory_Trims()
    {
        // Very small budget to force trimming
        var manager = new ContextWindowManager(50);
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 100; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"这是第 {i} 条很长的测试消息，包含大量文字"));
        }

        var result = manager.TrimHistory(messages, "system prompt");

        Assert.True(result.Count < messages.Count);
    }
}
