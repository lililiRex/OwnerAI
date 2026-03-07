namespace OwnerAI.Agent.Planner;

/// <summary>
/// 规划结果
/// </summary>
public sealed record PlanResult
{
    public required string Thought { get; init; }
    public string? Action { get; init; }
    public string? ActionInput { get; init; }
    public bool IsComplete { get; init; }
}

/// <summary>
/// 规划器接口
/// </summary>
public interface IPlanner
{
    string Name { get; }
    Task<PlanResult> PlanAsync(AgentContext context, CancellationToken ct);
}
