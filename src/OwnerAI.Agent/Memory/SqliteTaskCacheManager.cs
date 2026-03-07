using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Memory;

/// <summary>
/// 基于 SQLite + FTS5 的任务缓存管理器 — 纯 .NET 实现，无外部依赖
/// <para>使用 FTS5 全文检索做候选召回，字符 n-gram 余弦相似度做精排</para>
/// <para>三表结构完整持久化到 SQLite 数据库</para>
/// </summary>
public sealed class SqliteTaskCacheManager : ITaskCacheManager, IDisposable
{
    /// <summary>精确命中阈值</summary>
    private const float ExactHitThreshold = 0.95f;

    /// <summary>参考命中阈值</summary>
    private const float ReferenceHitThreshold = 0.7f;

    /// <summary>向量维度 — 字符 n-gram 哈希桶数</summary>
    private const int EmbeddingDimensions = 256;

    /// <summary>缓存最大条目数</summary>
    private const int MaxCacheSize = 2000;

    private readonly SqliteConnection _connection;
    private readonly InMemoryTaskCacheManager _hotCache;
    private readonly ILogger<SqliteTaskCacheManager> _logger;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public SqliteTaskCacheManager(
        InMemoryTaskCacheManager hotCache,
        IOptions<Mem0Config> config,
        ILogger<SqliteTaskCacheManager> logger)
    {
        _hotCache = hotCache;
        _logger = logger;

        var dbPath = Path.Combine(config.Value.InstallPath, "task_cache.db");
        var dbDir = Path.GetDirectoryName(dbPath);
        if (dbDir is not null && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();

        _logger.LogInformation("[SqliteCache] 已初始化, 数据库: {Path}", dbPath);
    }

    /// <summary>
    /// 初始化数据库表结构 — 三表 + FTS5 全文索引
    /// </summary>
    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            -- 表1: 对话过程 (核心)
            CREATE TABLE IF NOT EXISTS task_cache (
                id             TEXT PRIMARY KEY,
                query          TEXT NOT NULL,
                result         TEXT NOT NULL,
                is_idempotent  INTEGER NOT NULL DEFAULT 0,
                embedding      BLOB,
                created_at     TEXT NOT NULL
            );

            -- 表2: 思考过程
            CREATE TABLE IF NOT EXISTS thinking_steps (
                cache_id  TEXT NOT NULL,
                round     INTEGER NOT NULL,
                reasoning TEXT NOT NULL,
                FOREIGN KEY (cache_id) REFERENCES task_cache(id) ON DELETE CASCADE
            );

            -- 表3: 工作过程
            CREATE TABLE IF NOT EXISTS work_steps (
                cache_id   TEXT NOT NULL,
                round      INTEGER NOT NULL,
                tool_name  TEXT NOT NULL,
                parameters TEXT,
                result     TEXT,
                success    INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (cache_id) REFERENCES task_cache(id) ON DELETE CASCADE
            );

            -- FTS5 全文索引 — 用于候选召回
            CREATE VIRTUAL TABLE IF NOT EXISTS task_cache_fts USING fts5(
                query,
                content='task_cache',
                content_rowid='rowid',
                tokenize='unicode61'
            );

            -- 触发器: 保持 FTS 索引同步
            CREATE TRIGGER IF NOT EXISTS task_cache_ai AFTER INSERT ON task_cache BEGIN
                INSERT INTO task_cache_fts(rowid, query) VALUES (new.rowid, new.query);
            END;

            CREATE TRIGGER IF NOT EXISTS task_cache_ad AFTER DELETE ON task_cache BEGIN
                INSERT INTO task_cache_fts(task_cache_fts, rowid, query) VALUES ('delete', old.rowid, old.query);
            END;

            -- 索引
            CREATE INDEX IF NOT EXISTS idx_thinking_cache_id ON thinking_steps(cache_id);
            CREATE INDEX IF NOT EXISTS idx_work_cache_id ON work_steps(cache_id);
            CREATE INDEX IF NOT EXISTS idx_task_cache_created ON task_cache(created_at);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<TaskCacheSearchResult?> SearchAsync(string query, CancellationToken ct = default)
    {
        // 优先查内存热缓存
        var hotResult = await _hotCache.SearchAsync(query, ct);
        if (hotResult is { HitMode: TaskCacheHitMode.ExactHit })
            return hotResult;

        try
        {
            // 1. 精确匹配 — 查询文本完全相同
            var exactEntry = FindExactMatch(query);
            if (exactEntry is not null)
            {
                var hitMode = exactEntry.IsIdempotent
                    ? TaskCacheHitMode.ExactHit
                    : TaskCacheHitMode.ReferenceHit;

                _logger.LogInformation(
                    "[SqliteCache] ExactMatch: mode={Mode}, query=\"{Query}\"",
                    hitMode, Truncate(query, 50));

                return new TaskCacheSearchResult
                {
                    Entry = exactEntry,
                    Score = 1.0f,
                    HitMode = hitMode,
                };
            }

            // 2. FTS5 候选召回 + 余弦相似度精排
            var candidates = FindFtsCandidates(query, topK: 10);
            if (candidates.Count == 0)
                return hotResult; // FTS 无结果，返回热缓存结果（可能是 ReferenceHit）

            var queryEmbedding = ComputeSimpleEmbedding(query);

            TaskCacheEntry? bestEntry = null;
            var bestScore = 0f;

            foreach (var (entry, embedding) in candidates)
            {
                var score = CosineSimilarity(queryEmbedding, embedding);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestEntry = entry;
                }
            }

            // 与热缓存结果取最优
            if (hotResult is not null && hotResult.Score >= bestScore)
                return hotResult;

            if (bestEntry is null || bestScore < ReferenceHitThreshold)
                return hotResult;

            var mode = bestScore >= ExactHitThreshold && bestEntry.IsIdempotent
                ? TaskCacheHitMode.ExactHit
                : TaskCacheHitMode.ReferenceHit;

            _logger.LogInformation(
                "[SqliteCache] Hit: score={Score:F3}, mode={Mode}, query=\"{Query}\"",
                bestScore, mode, Truncate(query, 50));

            return new TaskCacheSearchResult
            {
                Entry = bestEntry,
                Score = bestScore,
                HitMode = mode,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SqliteCache] 搜索失败，回退到内存缓存");
            return hotResult;
        }
    }

    public async Task StoreAsync(TaskCacheEntry entry, CancellationToken ct = default)
    {
        // 始终写入内存热缓存
        await _hotCache.StoreAsync(entry, ct);

        try
        {
            // 容量控制
            EnforceCapacity();

            var embedding = ComputeSimpleEmbedding(entry.Query);
            var embeddingBytes = EmbeddingToBytes(embedding);

            using var transaction = _connection.BeginTransaction();

            // 表1: 对话过程
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT OR REPLACE INTO task_cache (id, query, result, is_idempotent, embedding, created_at)
                    VALUES ($id, $query, $result, $idempotent, $embedding, $created_at)
                    """;
                cmd.Parameters.AddWithValue("$id", entry.Id);
                cmd.Parameters.AddWithValue("$query", entry.Query);
                cmd.Parameters.AddWithValue("$result", entry.Result);
                cmd.Parameters.AddWithValue("$idempotent", entry.IsIdempotent ? 1 : 0);
                cmd.Parameters.AddWithValue("$embedding", embeddingBytes);
                cmd.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            // 表2: 思考过程
            foreach (var step in entry.ThinkingSteps)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO thinking_steps (cache_id, round, reasoning)
                    VALUES ($cache_id, $round, $reasoning)
                    """;
                cmd.Parameters.AddWithValue("$cache_id", entry.Id);
                cmd.Parameters.AddWithValue("$round", step.Round);
                cmd.Parameters.AddWithValue("$reasoning", step.Reasoning);
                cmd.ExecuteNonQuery();
            }

            // 表3: 工作过程
            foreach (var step in entry.WorkSteps)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO work_steps (cache_id, round, tool_name, parameters, result, success)
                    VALUES ($cache_id, $round, $tool_name, $parameters, $result, $success)
                    """;
                cmd.Parameters.AddWithValue("$cache_id", entry.Id);
                cmd.Parameters.AddWithValue("$round", step.Round);
                cmd.Parameters.AddWithValue("$tool_name", step.ToolName);
                cmd.Parameters.AddWithValue("$parameters", (object?)step.Parameters ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$result", (object?)step.Result ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$success", step.Success ? 1 : 0);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();

            _logger.LogInformation(
                "[SqliteCache] Stored: id={Id}, query=\"{Query}\", idempotent={Idempotent}",
                entry.Id, Truncate(entry.Query, 50), entry.IsIdempotent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SqliteCache] 存储失败，仅保留内存缓存");
        }
    }

    // ── 查询方法 ────────────────────────────────────────────

    /// <summary>
    /// 精确匹配 — 归一化后的查询文本完全相同
    /// </summary>
    private TaskCacheEntry? FindExactMatch(string query)
    {
        var normalized = NormalizeQuery(query);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM task_cache WHERE LOWER(TRIM(query)) = $query LIMIT 1";
        cmd.Parameters.AddWithValue("$query", normalized);

        var id = cmd.ExecuteScalar() as string;
        return id is not null ? LoadEntry(id) : null;
    }

    /// <summary>
    /// FTS5 候选召回 — 返回候选条目及其嵌入向量
    /// </summary>
    private List<(TaskCacheEntry Entry, float[] Embedding)> FindFtsCandidates(string query, int topK)
    {
        var results = new List<(TaskCacheEntry, float[])>();

        // 构造 FTS5 查询词（简单分词 + OR 连接）
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
            return results;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.embedding
            FROM task_cache_fts f
            JOIN task_cache t ON t.rowid = f.rowid
            WHERE task_cache_fts MATCH $query
            ORDER BY bm25(task_cache_fts)
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", ftsQuery);
        cmd.Parameters.AddWithValue("$limit", topK);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var embeddingBytes = reader.IsDBNull(1) ? null : (byte[])reader[1];

            if (embeddingBytes is null) continue;

            var embedding = BytesToEmbedding(embeddingBytes);
            var entry = LoadEntry(id);
            if (entry is not null)
                results.Add((entry, embedding));
        }

        return results;
    }

    /// <summary>
    /// 从数据库加载完整的 TaskCacheEntry（含表2、表3）
    /// </summary>
    private TaskCacheEntry? LoadEntry(string id)
    {
        // 表1
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT query, result, is_idempotent, created_at FROM task_cache WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var query = reader.GetString(0);
        var result = reader.GetString(1);
        var isIdempotent = reader.GetInt32(2) != 0;
        var createdAt = DateTimeOffset.TryParse(reader.GetString(3), out var dt)
            ? dt : DateTimeOffset.Now;

        reader.Close();

        // 表2: 思考过程
        var thinkingSteps = new List<ThinkingStep>();
        using (var cmd2 = _connection.CreateCommand())
        {
            cmd2.CommandText = "SELECT round, reasoning FROM thinking_steps WHERE cache_id = $id ORDER BY round";
            cmd2.Parameters.AddWithValue("$id", id);
            using var r2 = cmd2.ExecuteReader();
            while (r2.Read())
            {
                thinkingSteps.Add(new ThinkingStep
                {
                    Round = r2.GetInt32(0),
                    Reasoning = r2.GetString(1),
                });
            }
        }

        // 表3: 工作过程
        var workSteps = new List<WorkStep>();
        using (var cmd3 = _connection.CreateCommand())
        {
            cmd3.CommandText = "SELECT round, tool_name, parameters, result, success FROM work_steps WHERE cache_id = $id ORDER BY round";
            cmd3.Parameters.AddWithValue("$id", id);
            using var r3 = cmd3.ExecuteReader();
            while (r3.Read())
            {
                workSteps.Add(new WorkStep
                {
                    Round = r3.GetInt32(0),
                    ToolName = r3.GetString(1),
                    Parameters = r3.IsDBNull(2) ? null : r3.GetString(2),
                    Result = r3.IsDBNull(3) ? null : r3.GetString(3),
                    Success = r3.GetInt32(4) != 0,
                });
            }
        }

        return new TaskCacheEntry
        {
            Id = id,
            Query = query,
            Result = result,
            IsIdempotent = isIdempotent,
            CreatedAt = createdAt,
            ThinkingSteps = thinkingSteps,
            WorkSteps = workSteps,
        };
    }

    // ── 容量控制 ────────────────────────────────────────────

    private void EnforceCapacity()
    {
        using var countCmd = _connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM task_cache";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());

        if (count < MaxCacheSize) return;

        // 删除最旧的 10% 条目
        var deleteCount = MaxCacheSize / 10;
        using var deleteCmd = _connection.CreateCommand();
        deleteCmd.CommandText = """
            DELETE FROM task_cache WHERE id IN (
                SELECT id FROM task_cache ORDER BY created_at ASC LIMIT $limit
            )
            """;
        deleteCmd.Parameters.AddWithValue("$limit", deleteCount);
        deleteCmd.ExecuteNonQuery();

        _logger.LogInformation("[SqliteCache] 容量控制: 删除了 {Count} 条最旧记录", deleteCount);
    }

    // ── FTS 查询构建 ────────────────────────────────────────

    /// <summary>
    /// 将查询文本转为 FTS5 MATCH 表达式 — 提取关键词用 OR 连接
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        // 移除常见停用词和标点，按空格/标点分词
        var tokens = query
            .ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '，', '。', '？', '！', '、', '：', '；',
                     ',', '.', '?', '!', ':', ';', '(', ')', '[', ']', '{', '}',
                     '"', '"', '"', '\'', '「', '」'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2) // 过滤单字符
            .Distinct()
            .Take(20) // 限制词数
            .ToList();

        if (tokens.Count == 0) return "";

        // FTS5 OR 查询
        return string.Join(" OR ", tokens.Select(t => $"\"{t}\""));
    }

    /// <summary>
    /// 归一化查询文本 — 用于精确匹配
    /// </summary>
    private static string NormalizeQuery(string query)
    {
        return query.ToLowerInvariant().Trim();
    }

    // ── 嵌入计算 ────────────────────────────────────────────

    /// <summary>
    /// 简单字符级 embedding — 字符 n-gram 的 bag-of-words 向量化
    /// <para>与 InMemoryTaskCacheManager 使用相同算法，确保一致性</para>
    /// </summary>
    private static float[] ComputeSimpleEmbedding(string text)
    {
        var embedding = new float[EmbeddingDimensions];
        var normalized = text.ToLowerInvariant().Trim();

        // 字符 bi-gram hash
        for (var i = 0; i < normalized.Length - 1; i++)
        {
            var hash = HashCode.Combine(normalized[i], normalized[i + 1]);
            var index = ((hash % EmbeddingDimensions) + EmbeddingDimensions) % EmbeddingDimensions;
            embedding[index] += 1f;
        }

        // 单字 hash（补充单字特征）
        foreach (var ch in normalized)
        {
            var index = ((ch.GetHashCode() % EmbeddingDimensions) + EmbeddingDimensions) % EmbeddingDimensions;
            embedding[index] += 0.5f;
        }

        // L2 归一化
        var magnitude = MathF.Sqrt(embedding.Sum(x => x * x));
        if (magnitude > 0)
        {
            for (var i = 0; i < EmbeddingDimensions; i++)
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

    // ── 嵌入序列化 ─────────────────────────────────────────

    private static byte[] EmbeddingToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToEmbedding(byte[] bytes)
    {
        var embedding = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
        return embedding;
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
