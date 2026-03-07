using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;

namespace OwnerAI.Gateway.Sessions;

/// <summary>
/// 会话对话历史 — SQLite 持久化 + 内存热缓存
/// <para>为 Agent 提供多轮对话上下文，使模型具备短期记忆能力</para>
/// <para>重启后自动从 SQLite 恢复历史，保证对话连续性</para>
/// </summary>
public sealed class ConversationHistory : IDisposable
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _cache = new();
    private readonly SqliteConnection _connection;
    private readonly ILogger<ConversationHistory> _logger;

    public ConversationHistory(IOptions<Mem0Config> config, ILogger<ConversationHistory> logger)
    {
        _logger = logger;

        var dbPath = Path.Combine(config.Value.InstallPath, "conversation_history.db");
        var dbDir = Path.GetDirectoryName(dbPath);
        if (dbDir is not null && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeDatabase();
        _logger.LogInformation("[ConversationHistory] 已初始化, 数据库: {Path}", dbPath);
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS conversation_messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT    NOT NULL,
                role        TEXT    NOT NULL,
                content     TEXT    NOT NULL,
                created_at  TEXT    NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_conv_session
                ON conversation_messages(session_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>获取指定会话的对话历史（优先从内存缓存读取）</summary>
    public IReadOnlyList<ChatMessage> GetMessages(string sessionId)
    {
        // 内存缓存命中
        if (_cache.TryGetValue(sessionId, out var cached))
            return cached.AsReadOnly();

        // 从 SQLite 加载并缓存
        var messages = LoadFromDatabase(sessionId);
        if (messages.Count > 0)
        {
            _cache[sessionId] = messages;
            _logger.LogInformation(
                "[ConversationHistory] 从数据库恢复会话 {Session}, {Count} 条消息",
                sessionId, messages.Count);
        }

        return messages.AsReadOnly();
    }

    /// <summary>追加一条消息到会话历史（同时写入内存和 SQLite）</summary>
    public void Append(string sessionId, ChatMessage message)
    {
        // 写入内存缓存
        var list = _cache.GetOrAdd(sessionId, _ => []);
        lock (list)
        {
            list.Add(message);
        }

        // 写入 SQLite
        SaveToDatabase(sessionId, message);
    }

    /// <summary>清除指定会话的历史</summary>
    public void Clear(string sessionId)
    {
        _cache.TryRemove(sessionId, out _);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM conversation_messages WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取最近 N 条消息（跨会话） — 用于程序重启后恢复 UI 聊天记录
    /// </summary>
    public IReadOnlyList<(string Role, string Content, DateTimeOffset CreatedAt)> GetRecentMessages(int count = 10)
    {
        var results = new List<(string Role, string Content, DateTimeOffset CreatedAt)>();

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT role, content, created_at FROM conversation_messages
                ORDER BY id DESC
                LIMIT @count
                """;
            cmd.Parameters.AddWithValue("@count", count);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var role = reader.GetString(0);
                var content = reader.GetString(1);
                var createdAt = DateTimeOffset.TryParse(reader.GetString(2), out var dt)
                    ? dt : DateTimeOffset.Now;
                results.Add((role, content, createdAt));
            }

            // 数据库返回倒序，翻转为时间正序
            results.Reverse();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ConversationHistory] 读取最近消息失败");
        }

        return results;
    }

    private void SaveToDatabase(string sessionId, ChatMessage message)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO conversation_messages (session_id, role, content)
                VALUES (@sid, @role, @content)
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@role", message.Role.Value);
            cmd.Parameters.AddWithValue("@content", message.Text ?? string.Empty);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ConversationHistory] 写入数据库失败, session={Session}", sessionId);
        }
    }

    private List<ChatMessage> LoadFromDatabase(string sessionId)
    {
        var messages = new List<ChatMessage>();

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT role, content FROM conversation_messages
                WHERE session_id = @sid
                ORDER BY id ASC
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var role = new ChatRole(reader.GetString(0));
                var content = reader.GetString(1);
                messages.Add(new ChatMessage(role, content));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ConversationHistory] 读取数据库失败, session={Session}", sessionId);
        }

        return messages;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
