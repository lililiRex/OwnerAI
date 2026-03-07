using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Memory;

/// <summary>
/// 基于 Mem0 向量数据库的任务缓存管理器 — 三表缓存的真实实现
/// <para>当 Mem0 服务不可用时自动回退到内存缓存</para>
/// </summary>
public sealed class Mem0TaskCacheManager : ITaskCacheManager, IDisposable
{
    /// <summary>精确命中阈值</summary>
    private const float ExactHitThreshold = 0.95f;

    /// <summary>参考命中阈值</summary>
    private const float ReferenceHitThreshold = 0.7f;

    private readonly Mem0ServerManager _serverManager;
    private readonly InMemoryTaskCacheManager _fallback;
    private readonly HttpClient _httpClient;
    private readonly ILogger<Mem0TaskCacheManager> _logger;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public Mem0TaskCacheManager(
        Mem0ServerManager serverManager,
        InMemoryTaskCacheManager fallback,
        IOptions<Mem0Config> config,
        ILogger<Mem0TaskCacheManager> logger)
    {
        _serverManager = serverManager;
        _fallback = fallback;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Value.BaseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task<TaskCacheSearchResult?> SearchAsync(string query, CancellationToken ct = default)
    {
        // Mem0 不可用时回退到内存缓存
        if (!_serverManager.IsReady)
            return await _fallback.SearchAsync(query, ct);

        try
        {
            var request = new Mem0SearchRequest { Query = query, TopK = 3, UserId = "owner" };
            var response = await _httpClient.PostAsJsonAsync("/search", request, s_jsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<Mem0SearchResponse>(s_jsonOptions, ct);
            if (result?.Results is not { Count: > 0 })
                return null;

            // 取最高分的结果
            var best = result.Results[0];
            var score = best.Score;

            if (score < ReferenceHitThreshold)
                return null;

            // 从 Mem0 metadata 中恢复三表数据
            var cacheEntry = DeserializeCacheEntry(best);

            var hitMode = score >= ExactHitThreshold && cacheEntry.IsIdempotent
                ? TaskCacheHitMode.ExactHit
                : TaskCacheHitMode.ReferenceHit;

            _logger.LogInformation(
                "[Mem0Cache] Hit: score={Score:F3}, mode={Mode}, query=\"{Query}\"",
                score, hitMode, Truncate(query, 50));

            return new TaskCacheSearchResult
            {
                Entry = cacheEntry,
                Score = score,
                HitMode = hitMode,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mem0Cache] 搜索失败，回退到内存缓存");
            return await _fallback.SearchAsync(query, ct);
        }
    }

    public async Task StoreAsync(TaskCacheEntry entry, CancellationToken ct = default)
    {
        // 始终写入内存缓存（作为热缓存）
        await _fallback.StoreAsync(entry, ct);

        // Mem0 不可用时仅使用内存
        if (!_serverManager.IsReady)
            return;

        try
        {
            // 将三表数据序列化到 Mem0 的 content + metadata 中
            var content = BuildMem0Content(entry);
            var metadata = BuildMem0Metadata(entry);

            var request = new Mem0StoreRequest
            {
                Content = content,
                UserId = "owner",
                Metadata = metadata,
            };

            var response = await _httpClient.PostAsJsonAsync("/store", request, s_jsonOptions, ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("[Mem0Cache] 已存储到 Mem0: query=\"{Query}\"", Truncate(entry.Query, 50));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mem0Cache] 存储到 Mem0 失败，仅保留内存缓存");
        }
    }

    /// <summary>
    /// 构建存入 Mem0 的内容文本 — 用于向量化检索（表1核心）
    /// </summary>
    private static string BuildMem0Content(TaskCacheEntry entry)
    {
        return $"问题: {entry.Query}\n结果: {entry.Result}";
    }

    /// <summary>
    /// 将表2/表3数据序列化到 Mem0 metadata 中
    /// </summary>
    private static Dictionary<string, object> BuildMem0Metadata(TaskCacheEntry entry)
    {
        var metadata = new Dictionary<string, object>
        {
            ["cache_id"] = entry.Id,
            ["query"] = entry.Query,
            ["result"] = entry.Result,
            ["is_idempotent"] = entry.IsIdempotent,
            ["created_at"] = entry.CreatedAt.ToString("o"),
        };

        // 表2: 思考过程
        if (entry.ThinkingSteps.Count > 0)
        {
            metadata["thinking_steps"] = JsonSerializer.Serialize(entry.ThinkingSteps, s_jsonOptions);
        }

        // 表3: 工作过程
        if (entry.WorkSteps.Count > 0)
        {
            metadata["work_steps"] = JsonSerializer.Serialize(entry.WorkSteps, s_jsonOptions);
        }

        return metadata;
    }

    /// <summary>
    /// 从 Mem0 搜索结果反序列化为 TaskCacheEntry
    /// </summary>
    private TaskCacheEntry DeserializeCacheEntry(Mem0SearchItem item)
    {
        var meta = item.Metadata ?? new Dictionary<string, JsonElement>();

        var query = GetMetaString(meta, "query") ?? item.Memory ?? "";
        var result = GetMetaString(meta, "result") ?? "";
        var isIdempotent = GetMetaBool(meta, "is_idempotent");
        var cacheId = GetMetaString(meta, "cache_id") ?? item.Id ?? Guid.NewGuid().ToString("N");

        // 反序列化表2: 思考过程
        var thinkingSteps = DeserializeList<ThinkingStep>(meta, "thinking_steps");

        // 反序列化表3: 工作过程
        var workSteps = DeserializeList<WorkStep>(meta, "work_steps");

        var createdAt = GetMetaString(meta, "created_at") is { } createdStr
            && DateTimeOffset.TryParse(createdStr, out var parsedDate)
            ? parsedDate
            : DateTimeOffset.Now;

        return new TaskCacheEntry
        {
            Id = cacheId,
            Query = query,
            Result = result,
            IsIdempotent = isIdempotent,
            CreatedAt = createdAt,
            ThinkingSteps = thinkingSteps,
            WorkSteps = workSteps,
        };
    }

    private static string? GetMetaString(Dictionary<string, JsonElement> meta, string key)
    {
        return meta.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static bool GetMetaBool(Dictionary<string, JsonElement> meta, string key)
    {
        if (!meta.TryGetValue(key, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) && b,
            _ => false,
        };
    }

    private List<T> DeserializeList<T>(Dictionary<string, JsonElement> meta, string key)
    {
        if (!meta.TryGetValue(key, out var el))
            return [];

        try
        {
            var json = el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : el.GetRawText();

            if (string.IsNullOrEmpty(json))
                return [];

            return JsonSerializer.Deserialize<List<T>>(json, s_jsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Mem0Cache] 反序列化 {Key} 失败", key);
            return [];
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    // ── Mem0 REST API 模型 ─────────────────────────────────

    private sealed class Mem0SearchRequest
    {
        public string Query { get; init; } = "";
        public int TopK { get; init; } = 5;
        public string UserId { get; init; } = "owner";
    }

    private sealed class Mem0StoreRequest
    {
        public string Content { get; init; } = "";
        public string UserId { get; init; } = "owner";
        public Dictionary<string, object>? Metadata { get; init; }
    }

    private sealed class Mem0SearchResponse
    {
        public List<Mem0SearchItem>? Results { get; init; }
    }

    private sealed class Mem0SearchItem
    {
        public string? Id { get; init; }
        public string? Memory { get; init; }
        public float Score { get; init; }
        public Dictionary<string, JsonElement>? Metadata { get; init; }
        public string? CreatedAt { get; init; }
    }
}
