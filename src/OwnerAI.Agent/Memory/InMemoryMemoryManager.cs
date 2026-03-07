using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Memory;

/// <summary>
/// 内存记忆管理器 — Phase 1 最小实现，后续替换为 SQLite + 向量存储
/// </summary>
public sealed class InMemoryMemoryManager : IMemoryManager
{
    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string query, int topK = 5, MemoryLevel? minLevel = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

    public Task<MemoryEntry?> GetUserProfileAsync(string userId, CancellationToken ct = default)
        => Task.FromResult<MemoryEntry?>(null);

    public Task IngestConversationAsync(
        string sessionId, string userMessage, string assistantReply, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
        => Task.FromResult(entry.Id);
}
