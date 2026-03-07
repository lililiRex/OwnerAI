using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Memory;

/// <summary>
/// 内存任务缓存管理器 — Phase 1 实现，使用简单余弦相似度
/// <para>后续替换为 Mem0 向量数据库实现</para>
/// </summary>
public sealed class InMemoryTaskCacheManager(ILogger<InMemoryTaskCacheManager> logger) : ITaskCacheManager
{
    /// <summary>精确命中阈值 — 相似度 ≥ 0.95 且任务幂等时跳过执行</summary>
    private const float ExactHitThreshold = 0.95f;

    /// <summary>参考命中阈值 — 相似度 ≥ 0.7 时注入思考链</summary>
    private const float ReferenceHitThreshold = 0.7f;

    /// <summary>缓存最大容量</summary>
    private const int MaxCacheSize = 500;

    private readonly ConcurrentDictionary<string, (TaskCacheEntry Entry, float[] Embedding)> _cache = new();

    public Task<TaskCacheSearchResult?> SearchAsync(string query, CancellationToken ct = default)
    {
        if (_cache.IsEmpty)
            return Task.FromResult<TaskCacheSearchResult?>(null);

        var queryEmbedding = ComputeSimpleEmbedding(query);

        TaskCacheEntry? bestEntry = null;
        var bestScore = 0f;

        foreach (var (_, (entry, embedding)) in _cache)
        {
            var score = CosineSimilarity(queryEmbedding, embedding);
            if (score > bestScore)
            {
                bestScore = score;
                bestEntry = entry;
            }
        }

        if (bestEntry is null || bestScore < ReferenceHitThreshold)
            return Task.FromResult<TaskCacheSearchResult?>(null);

        // 确定命中模式
        var hitMode = bestScore >= ExactHitThreshold && bestEntry.IsIdempotent
            ? TaskCacheHitMode.ExactHit
            : TaskCacheHitMode.ReferenceHit;

        logger.LogInformation(
            "[TaskCache] Hit: score={Score:F3}, mode={Mode}, query=\"{Query}\", cached=\"{Cached}\"",
            bestScore, hitMode, Truncate(query, 50), Truncate(bestEntry.Query, 50));

        return Task.FromResult<TaskCacheSearchResult?>(new TaskCacheSearchResult
        {
            Entry = bestEntry,
            Score = bestScore,
            HitMode = hitMode,
        });
    }

    public Task StoreAsync(TaskCacheEntry entry, CancellationToken ct = default)
    {
        // 容量控制 — 超出时移除最旧的
        if (_cache.Count >= MaxCacheSize)
        {
            var oldest = _cache
                .OrderBy(kv => kv.Value.Entry.CreatedAt)
                .FirstOrDefault();
            if (oldest.Key is not null)
                _cache.TryRemove(oldest.Key, out _);
        }

        var embedding = ComputeSimpleEmbedding(entry.Query);
        _cache[entry.Id] = (entry, embedding);

        logger.LogInformation(
            "[TaskCache] Stored: id={Id}, query=\"{Query}\", idempotent={Idempotent}, " +
            "thinkingSteps={ThinkingCount}, workSteps={WorkCount}",
            entry.Id, Truncate(entry.Query, 50), entry.IsIdempotent,
            entry.ThinkingSteps.Count, entry.WorkSteps.Count);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 简单字符级 embedding — Phase 1 用于演示，后续替换为真实 embedding 模型
    /// <para>基于字符 n-gram 的 bag-of-words 向量化</para>
    /// </summary>
    private static float[] ComputeSimpleEmbedding(string text)
    {
        const int dimensions = 256;
        var embedding = new float[dimensions];
        var normalized = text.ToLowerInvariant().Trim();

        // 字符 bi-gram hash
        for (var i = 0; i < normalized.Length - 1; i++)
        {
            var hash = HashCode.Combine(normalized[i], normalized[i + 1]);
            var index = ((hash % dimensions) + dimensions) % dimensions;
            embedding[index] += 1f;
        }

        // 单字 hash（补充单字特征）
        foreach (var ch in normalized)
        {
            var index = ((ch.GetHashCode() % dimensions) + dimensions) % dimensions;
            embedding[index] += 0.5f;
        }

        // L2 归一化
        var magnitude = MathF.Sqrt(embedding.Sum(x => x * x));
        if (magnitude > 0)
        {
            for (var i = 0; i < dimensions; i++)
                embedding[i] /= magnitude;
        }

        return embedding;
    }

    /// <summary>
    /// 余弦相似度
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        var dot = 0f;
        var magA = 0f;
        var magB = 0f;

        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denominator > 0 ? dot / denominator : 0f;
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");
}
