namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 记忆层级 — 对应 TiMem 的 5 级 TMT
/// </summary>
public enum MemoryLevel
{
    /// <summary>L1: 对话碎片 (实时, 每 2-3 轮提取)</summary>
    Fragment = 1,
    /// <summary>L2: 会话摘要 (会话结束时固化)</summary>
    Session = 2,
    /// <summary>L3: 日报摘要 (每日固化)</summary>
    Daily = 3,
    /// <summary>L4: 周报摘要 (每周固化)</summary>
    Weekly = 4,
    /// <summary>L5: 用户画像 (月度固化, 最稳定)</summary>
    Profile = 5,
}

/// <summary>
/// 记忆条目 — 携带层级和时序信息
/// </summary>
public sealed record MemoryEntry
{
    public required string Id { get; init; }
    public required MemoryLevel Level { get; init; }
    public required string Content { get; init; }
    public float[]? Embedding { get; init; }
    public string UserId { get; init; } = "owner";
    public string? SessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? TemporalStart { get; init; }
    public DateTimeOffset? TemporalEnd { get; init; }
    public float ImportanceScore { get; init; }
    public string? Source { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// 记忆搜索结果
/// </summary>
public sealed record MemorySearchResult
{
    public required MemoryEntry Entry { get; init; }
    public float Score { get; init; }
    public string? MatchType { get; init; }
}

/// <summary>
/// 记忆管理器接口
/// </summary>
public interface IMemoryManager
{
    /// <summary>搜索相关记忆</summary>
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string query,
        int topK = 5,
        MemoryLevel? minLevel = null,
        CancellationToken ct = default);

    /// <summary>获取用户画像 (L5)</summary>
    Task<MemoryEntry?> GetUserProfileAsync(string userId, CancellationToken ct = default);

    /// <summary>摄入对话到记忆</summary>
    Task IngestConversationAsync(
        string sessionId,
        string userMessage,
        string assistantReply,
        CancellationToken ct = default);

    /// <summary>存储记忆条目</summary>
    Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default);
}

/// <summary>
/// 向量存储接口
/// </summary>
public interface IVectorStore
{
    Task UpsertAsync(MemoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        float[] queryEmbedding,
        int topK = 5,
        MemoryLevel? level = null,
        CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>
/// 嵌入模型接口
/// </summary>
public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
    int Dimensions { get; }
}

/// <summary>
/// 记忆固化器接口
/// </summary>
public interface IMemoryConsolidator
{
    MemoryLevel TargetLevel { get; }
    Task ConsolidateAsync(CancellationToken ct = default);
}
