namespace OwnerAI.Gateway.Sessions;

/// <summary>
/// 会话信息
/// </summary>
public sealed record SessionInfo
{
    public required string Id { get; init; }
    public string UserId { get; init; } = "owner";
    public string? ChannelId { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; set; }
    public int TurnCount { get; set; }
    public bool IsActive => EndedAt is null;
}

/// <summary>
/// 会话管理接口
/// </summary>
public interface ISessionManager
{
    /// <summary>获取或创建会话</summary>
    Task<SessionInfo> GetOrCreateSessionAsync(string channelId, string senderId, CancellationToken ct);

    /// <summary>获取会话</summary>
    Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct);

    /// <summary>结束会话</summary>
    Task EndSessionAsync(string sessionId, CancellationToken ct);

    /// <summary>增加对话轮次</summary>
    Task IncrementTurnAsync(string sessionId, CancellationToken ct);

    /// <summary>获取活跃会话列表</summary>
    Task<IReadOnlyList<SessionInfo>> GetActiveSessionsAsync(CancellationToken ct);
}
