using Microsoft.Extensions.Logging;

namespace OwnerAI.Agent.Planner;

/// <summary>
/// ReAct 规划器 — 推理 + 行动循环 (Thought → Action → Observation)
/// </summary>
public sealed class ReActPlanner(ILogger<ReActPlanner> logger) : IPlanner
{
    public string Name => "ReAct";

    public Task<PlanResult> PlanAsync(AgentContext context, CancellationToken ct)
    {
        logger.LogDebug("[ReAct] Planning for session {Session}", context.SessionId);

        // ReAct 模式: 推理循环由 AgentExecutor 配合 LLM 驱动
        // Planner 仅提供初始规划提示
        var result = new PlanResult
        {
            Thought = $"分析用户请求: {context.UserMessage}",
            IsComplete = false,
        };

        return Task.FromResult(result);
    }
}
