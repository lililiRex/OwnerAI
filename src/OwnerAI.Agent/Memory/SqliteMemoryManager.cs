using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Memory;

/// <summary>
/// 基于 SQLite + FTS5 的长期记忆管理器 — 纯 .NET 实现，无外部依赖
/// <para>实现 TiMem 5 层记忆体系的持久化存储:</para>
/// <para>  L1: 对话碎片 (Fragment) — 每次对话自动提取</para>
/// <para>  L2: 会话摘要 (Session)  — 会话结束时固化</para>
/// <para>  L3: 日报摘要 (Daily)    — 每日固化</para>
/// <para>  L4: 周报摘要 (Weekly)   — 每周固化</para>
/// <para>  L5: 用户画像 (Profile)  — 月度固化</para>
/// <para>使用 FTS5 全文检索做候选召回，字符 n-gram 余弦相似度做精排</para>
/// </summary>
public sealed class SqliteMemoryManager : IMemoryManager, IDisposable
{
    /// <summary>向量维度 — 字符 n-gram 哈希桶数</summary>
    private const int EmbeddingDimensions = 256;

    /// <summary>搜索相似度下限 — 低于此分数不返回</summary>
    private const float MinSearchScore = 0.15f;

    /// <summary>记忆最大条目数</summary>
    private const int MaxMemoryEntries = 5000;

    /// <summary>对话碎片最大长度（字符）— 过长则截断</summary>
    private const int MaxFragmentLength = 500;

    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteMemoryManager> _logger;

    public SqliteMemoryManager(
        IOptions<Mem0Config> config,
        ILogger<SqliteMemoryManager> logger)
    {
        _logger = logger;

        var dbPath = Path.Combine(config.Value.InstallPath, "memory.db");
        var dbDir = Path.GetDirectoryName(dbPath);
        if (dbDir is not null && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("[Memory] 已初始化, 数据库: {Path}", dbPath);
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            -- 记忆主表
            CREATE TABLE IF NOT EXISTS memories (
                id               TEXT    PRIMARY KEY,
                level            INTEGER NOT NULL,
                content          TEXT    NOT NULL,
                embedding        BLOB,
                user_id          TEXT    NOT NULL DEFAULT 'owner',
                session_id       TEXT,
                created_at       TEXT    NOT NULL,
                temporal_start   TEXT,
                temporal_end     TEXT,
                importance_score REAL    NOT NULL DEFAULT 0,
                source           TEXT
            );

            -- FTS5 全文索引 — 候选召回
            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
                content,
                content='memories',
                content_rowid='rowid',
                tokenize='unicode61'
            );

            -- 触发器: 保持 FTS 索引同步
            CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
                INSERT INTO memories_fts(rowid, content) VALUES (new.rowid, new.content);
            END;

            CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
                INSERT INTO memories_fts(memories_fts, rowid, content) VALUES ('delete', old.rowid, old.content);
            END;

            -- 索引
            CREATE INDEX IF NOT EXISTS idx_memories_level    ON memories(level);
            CREATE INDEX IF NOT EXISTS idx_memories_user     ON memories(user_id);
            CREATE INDEX IF NOT EXISTS idx_memories_session  ON memories(session_id);
            CREATE INDEX IF NOT EXISTS idx_memories_created  ON memories(created_at);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── IMemoryManager 实现 ──────────────────────────────────

    /// <summary>
    /// 搜索相关记忆 — FTS5 候选召回 + 余弦相似度精排
    /// </summary>
    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string query, int topK = 5, MemoryLevel? minLevel = null, CancellationToken ct = default)
    {
        try
        {
            var results = new List<MemorySearchResult>();

            // 1. FTS5 候选召回
            var candidates = FindFtsCandidates(query, minLevel, topK: topK * 3);
            if (candidates.Count == 0)
                return Task.FromResult<IReadOnlyList<MemorySearchResult>>(results);

            // 2. 余弦相似度精排
            var queryEmbedding = ComputeSimpleEmbedding(query);

            foreach (var (entry, embedding) in candidates)
            {
                var score = CosineSimilarity(queryEmbedding, embedding);

                if (score >= MinSearchScore)
                {
                    // 高层级记忆给予加权 — L5 画像和 L4 周报比 L1 碎片更可靠
                    var levelBoost = (int)entry.Level * 0.02f;
                    var adjustedScore = Math.Min(score + levelBoost, 1.0f);

                    results.Add(new MemorySearchResult
                    {
                        Entry = entry,
                        Score = adjustedScore,
                        MatchType = "fts+cosine",
                    });
                }
            }

            // 按分数降序，取 topK
            var sorted = results
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .ToList();

            _logger.LogInformation(
                "[Memory] 搜索完成: query=\"{Query}\", 候选={Candidates}, 结果={Results}",
                Truncate(query, 50), candidates.Count, sorted.Count);

            return Task.FromResult<IReadOnlyList<MemorySearchResult>>(sorted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] 搜索失败");
            return Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);
        }
    }

    /// <summary>
    /// 获取用户画像 (L5) — 返回最新的画像记忆
    /// </summary>
    public Task<MemoryEntry?> GetUserProfileAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, content, importance_score, created_at, source
                FROM memories
                WHERE user_id = $user_id AND level = $level
                ORDER BY created_at DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$user_id", userId);
            cmd.Parameters.AddWithValue("$level", (int)MemoryLevel.Profile);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<MemoryEntry?>(null);

            var entry = new MemoryEntry
            {
                Id = reader.GetString(0),
                Level = MemoryLevel.Profile,
                Content = reader.GetString(1),
                UserId = userId,
                ImportanceScore = reader.GetFloat(2),
                CreatedAt = DateTimeOffset.TryParse(reader.GetString(3), out var dt)
                    ? dt : DateTimeOffset.Now,
                Source = reader.IsDBNull(4) ? null : reader.GetString(4),
            };

            return Task.FromResult<MemoryEntry?>(entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] 获取用户画像失败");
            return Task.FromResult<MemoryEntry?>(null);
        }
    }

    /// <summary>
    /// 摄入对话到记忆 — 自动提取 L1 对话碎片并存储
    /// <para>从用户消息和助手回复中提取关键信息作为记忆碎片</para>
    /// </summary>
    public async Task IngestConversationAsync(
        string sessionId, string userMessage, string assistantReply, CancellationToken ct = default)
    {
        try
        {
            // 构建对话碎片内容 — 保留用户意图和关键回复
            var fragmentContent = BuildFragment(userMessage, assistantReply);
            if (string.IsNullOrWhiteSpace(fragmentContent))
                return;

            // 计算重要性分数 — 简单启发式
            var importance = EstimateImportance(userMessage, assistantReply);

            var entry = new MemoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Level = MemoryLevel.Fragment,
                Content = fragmentContent,
                UserId = "owner",
                SessionId = sessionId,
                ImportanceScore = importance,
                Source = "conversation",
                TemporalStart = DateTimeOffset.Now,
            };

            await StoreAsync(entry, ct);

            _logger.LogDebug(
                "[Memory] 摄入对话碎片: importance={Importance:F2}, length={Length}",
                importance, fragmentContent.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] 摄入对话失败");
        }
    }

    /// <summary>
    /// 存储记忆条目 — 写入 SQLite 并建立索引
    /// </summary>
    public Task<string> StoreAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        try
        {
            EnforceCapacity();

            var embedding = ComputeSimpleEmbedding(entry.Content);
            var embeddingBytes = EmbeddingToBytes(embedding);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO memories
                    (id, level, content, embedding, user_id, session_id,
                     created_at, temporal_start, temporal_end, importance_score, source)
                VALUES
                    ($id, $level, $content, $embedding, $user_id, $session_id,
                     $created_at, $temporal_start, $temporal_end, $importance_score, $source)
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$level", (int)entry.Level);
            cmd.Parameters.AddWithValue("$content", entry.Content);
            cmd.Parameters.AddWithValue("$embedding", embeddingBytes);
            cmd.Parameters.AddWithValue("$user_id", entry.UserId);
            cmd.Parameters.AddWithValue("$session_id", (object?)entry.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$temporal_start",
                entry.TemporalStart.HasValue ? entry.TemporalStart.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("$temporal_end",
                entry.TemporalEnd.HasValue ? entry.TemporalEnd.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("$importance_score", entry.ImportanceScore);
            cmd.Parameters.AddWithValue("$source", (object?)entry.Source ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            _logger.LogDebug(
                "[Memory] 存储: id={Id}, level={Level}, importance={Score:F2}",
                entry.Id, entry.Level, entry.ImportanceScore);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] 存储失败: id={Id}", entry.Id);
        }

        return Task.FromResult(entry.Id);
    }

    // ── 对话碎片提取 ─────────────────────────────────────────

    /// <summary>
    /// 从对话中构建记忆碎片 — 提取关键信息
    /// </summary>
    private static string BuildFragment(string userMessage, string assistantReply)
    {
        // 过滤过短的对话（打招呼等无实质内容）
        if (userMessage.Length < 4 && assistantReply.Length < 20)
            return string.Empty;

        var user = Truncate(userMessage, MaxFragmentLength);
        var reply = Truncate(assistantReply, MaxFragmentLength);

        return $"用户: {user}\n助手: {reply}";
    }

    /// <summary>
    /// 估算对话重要性 — 简单启发式评分 [0, 1]
    /// <para>含有特定关键信息的对话获得更高分:</para>
    /// <list type="bullet">
    /// <item>包含个人信息（姓名、偏好、习惯）</item>
    /// <item>包含具体任务指令</item>
    /// <item>对话长度较长（深度交流）</item>
    /// <item>包含工具调用结果（实际操作）</item>
    /// </list>
    /// </summary>
    private static float EstimateImportance(string userMessage, string assistantReply)
    {
        var score = 0.3f; // 基础分

        var combined = userMessage + assistantReply;
        var length = combined.Length;

        // 长度加权 — 深度对话更重要
        if (length > 200) score += 0.1f;
        if (length > 500) score += 0.1f;

        // 个人信息关键词 — 用户画像线索
        ReadOnlySpan<string> personalKeywords =
            ["我叫", "我是", "我的名字", "我喜欢", "我不喜欢", "我习惯", "我经常",
             "我的工作", "我住在", "我的生日", "记住", "别忘了", "以后"];
        foreach (var kw in personalKeywords)
        {
            if (userMessage.Contains(kw, StringComparison.Ordinal))
            {
                score += 0.15f;
                break;
            }
        }

        // 任务指令关键词 — 实质操作
        ReadOnlySpan<string> taskKeywords =
            ["帮我", "请", "下载", "搜索", "创建", "打开", "运行", "安装",
             "写一个", "生成", "修改", "删除", "查找"];
        foreach (var kw in taskKeywords)
        {
            if (userMessage.Contains(kw, StringComparison.Ordinal))
            {
                score += 0.05f;
                break;
            }
        }

        // 助手回复质量 — 有实质内容更重要
        if (assistantReply.Length > 100) score += 0.05f;

        return Math.Min(score, 1.0f);
    }

    // ── FTS5 搜索 ────────────────────────────────────────────

    /// <summary>
    /// FTS5 候选召回 — 返回候选条目及其嵌入向量
    /// </summary>
    private List<(MemoryEntry Entry, float[] Embedding)> FindFtsCandidates(
        string query, MemoryLevel? minLevel, int topK)
    {
        var results = new List<(MemoryEntry, float[])>();

        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
            return results;

        // 构建 SQL — 可选层级过滤
        var levelFilter = minLevel.HasValue
            ? "AND m.level >= $min_level"
            : "";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.level, m.content, m.embedding, m.user_id, m.session_id,
                   m.created_at, m.temporal_start, m.temporal_end,
                   m.importance_score, m.source
            FROM memories_fts f
            JOIN memories m ON m.rowid = f.rowid
            WHERE memories_fts MATCH $query {levelFilter}
            ORDER BY bm25(memories_fts)
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", ftsQuery);
        cmd.Parameters.AddWithValue("$limit", topK);
        if (minLevel.HasValue)
            cmd.Parameters.AddWithValue("$min_level", (int)minLevel.Value);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var embeddingBytes = reader.IsDBNull(3) ? null : (byte[])reader[3];
            if (embeddingBytes is null) continue;

            var entry = ReadEntryFromRow(reader);
            var embedding = BytesToEmbedding(embeddingBytes);
            results.Add((entry, embedding));
        }

        return results;
    }

    /// <summary>
    /// 从 DataReader 行构建 MemoryEntry
    /// </summary>
    private static MemoryEntry ReadEntryFromRow(SqliteDataReader reader)
    {
        return new MemoryEntry
        {
            Id = reader.GetString(0),
            Level = (MemoryLevel)reader.GetInt32(1),
            Content = reader.GetString(2),
            // Embedding 由调用方单独处理
            UserId = reader.GetString(4),
            SessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = DateTimeOffset.TryParse(reader.GetString(6), out var dt)
                ? dt : DateTimeOffset.Now,
            TemporalStart = reader.IsDBNull(7) ? null
                : DateTimeOffset.TryParse(reader.GetString(7), out var ts) ? ts : null,
            TemporalEnd = reader.IsDBNull(8) ? null
                : DateTimeOffset.TryParse(reader.GetString(8), out var te) ? te : null,
            ImportanceScore = reader.GetFloat(9),
            Source = reader.IsDBNull(10) ? null : reader.GetString(10),
        };
    }

    // ── FTS 查询构建 ─────────────────────────────────────────

    /// <summary>
    /// 将查询文本转为 FTS5 MATCH 表达式 — 提取关键词用 OR 连接
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        var tokens = query
            .ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '，', '。', '？', '！', '、', '：', '；',
                     ',', '.', '?', '!', ':', ';', '(', ')', '[', ']', '{', '}',
                     '"', '"', '"', '\'', '「', '」'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct()
            .Take(20)
            .ToList();

        if (tokens.Count == 0) return "";

        return string.Join(" OR ", tokens.Select(t => $"\"{t}\""));
    }

    // ── 容量控制 ─────────────────────────────────────────────

    private void EnforceCapacity()
    {
        using var countCmd = _connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM memories";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());

        if (count < MaxMemoryEntries) return;

        // 删除最旧的 L1 碎片（优先清理低层级）
        var deleteCount = MaxMemoryEntries / 10;
        using var deleteCmd = _connection.CreateCommand();
        deleteCmd.CommandText = """
            DELETE FROM memories WHERE id IN (
                SELECT id FROM memories
                WHERE level = 1
                ORDER BY importance_score ASC, created_at ASC
                LIMIT $limit
            )
            """;
        deleteCmd.Parameters.AddWithValue("$limit", deleteCount);
        var deleted = deleteCmd.ExecuteNonQuery();

        _logger.LogInformation("[Memory] 容量控制: 删除了 {Count} 条低优先级碎片", deleted);
    }

    // ── 嵌入计算 ─────────────────────────────────────────────

    /// <summary>
    /// 简单字符级 embedding — 字符 n-gram 的 bag-of-words 向量化
    /// <para>与 SqliteTaskCacheManager 使用相同算法，确保一致性</para>
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

    // ── 嵌入序列化 ──────────────────────────────────────────

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
