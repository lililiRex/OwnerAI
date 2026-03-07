namespace OwnerAI.Security.Audit;

/// <summary>
/// 审计条目
/// </summary>
public sealed record AuditEntry
{
    public long Id { get; init; }
    public required string Operation { get; init; }
    public required string SessionId { get; init; }
    public string? Details { get; init; }
    public required string Result { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string? UserId { get; init; }
    public string? ChannelId { get; init; }
}

/// <summary>
/// 审计日志接口
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 100,
        CancellationToken ct = default);
    Task PruneAsync(int retentionDays, CancellationToken ct = default);
}
