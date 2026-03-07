using Microsoft.Extensions.AI;

namespace OwnerAI.Agent.Context;

/// <summary>
/// 上下文窗口管理 — Token 预算控制
/// </summary>
public sealed class ContextWindowManager
{
    private readonly int _tokenBudget;

    public ContextWindowManager(int tokenBudget = 128_000)
    {
        _tokenBudget = tokenBudget;
    }

    /// <summary>
    /// 裁剪对话历史以适应 Token 预算
    /// 保留系统提示 + 最近 N 轮对话
    /// </summary>
    public IReadOnlyList<ChatMessage> TrimHistory(
        IReadOnlyList<ChatMessage> messages,
        string systemPrompt)
    {
        // 简单估算: 1 个字符 ≈ 1-2 tokens (中文), ≈ 0.25 tokens (英文)
        // 保守按 2 tokens/char 估算
        var systemTokens = EstimateTokens(systemPrompt);
        var budget = _tokenBudget - systemTokens;

        if (budget <= 0)
            return [];

        var result = new List<ChatMessage>();
        var usedTokens = 0;

        // 从最新消息往回遍历
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            var tokens = EstimateTokens(msg.Text ?? string.Empty);

            if (usedTokens + tokens > budget)
                break;

            usedTokens += tokens;
            result.Insert(0, msg);
        }

        return result;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // 粗略估算: 中文每字 2 token, 英文每 4 字符 1 token
        var chineseChars = text.Count(c => c >= '\u4e00' && c <= '\u9fff');
        var otherChars = text.Length - chineseChars;
        return (chineseChars * 2) + (otherChars / 4) + 1;
    }
}
