namespace OwnerAI.Gateway.Health;

/// <summary>
/// 子系统健康状态
/// </summary>
public sealed record SubsystemHealth
{
    public required string Name { get; init; }
    public bool IsHealthy { get; init; }
    public string? Details { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 健康监控接口
/// </summary>
public interface IHealthMonitor
{
    Task<IReadOnlyList<SubsystemHealth>> CheckAllAsync(CancellationToken ct);
    void RegisterCheck(string name, Func<CancellationToken, Task<bool>> check);
}
