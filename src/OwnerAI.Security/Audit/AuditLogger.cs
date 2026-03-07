using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnerAI.Configuration;

namespace OwnerAI.Security.Audit;

/// <summary>
/// SQLite 审计日志实现
/// </summary>
public sealed class AuditLogger : IAuditLogger, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(IOptions<OwnerAIConfig> config, ILogger<AuditLogger> logger)
    {
        _logger = logger;

        var dbPath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "audit.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_log (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                operation   TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                details     TEXT,
                result      TEXT NOT NULL,
                user_id     TEXT,
                channel_id  TEXT,
                timestamp   TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit_log(timestamp);
            CREATE INDEX IF NOT EXISTS idx_audit_session ON audit_log(session_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_log (operation, session_id, details, result, user_id, channel_id, timestamp)
            VALUES (@op, @sid, @det, @res, @uid, @cid, @ts)
            """;
        cmd.Parameters.AddWithValue("@op", entry.Operation);
        cmd.Parameters.AddWithValue("@sid", entry.SessionId);
        cmd.Parameters.AddWithValue("@det", (object?)entry.Details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@res", entry.Result);
        cmd.Parameters.AddWithValue("@uid", (object?)entry.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cid", (object?)entry.ChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", entry.Timestamp.ToString("o"));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("[Audit] Logged: {Operation} → {Result}", entry.Operation, entry.Result);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, operation, session_id, details, result, user_id, channel_id, timestamp FROM audit_log WHERE 1=1";

        if (from.HasValue)
        {
            cmd.CommandText += " AND timestamp >= @from";
            cmd.Parameters.AddWithValue("@from", from.Value.ToString("o"));
        }
        if (to.HasValue)
        {
            cmd.CommandText += " AND timestamp <= @to";
            cmd.Parameters.AddWithValue("@to", to.Value.ToString("o"));
        }

        cmd.CommandText += " ORDER BY timestamp DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AuditEntry
            {
                Id = reader.GetInt64(0),
                Operation = reader.GetString(1),
                SessionId = reader.GetString(2),
                Details = reader.IsDBNull(3) ? null : reader.GetString(3),
                Result = reader.GetString(4),
                UserId = reader.IsDBNull(5) ? null : reader.GetString(5),
                ChannelId = reader.IsDBNull(6) ? null : reader.GetString(6),
                Timestamp = DateTimeOffset.Parse(reader.GetString(7)),
            });
        }
        return results;
    }

    public async Task PruneAsync(int retentionDays, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM audit_log WHERE timestamp < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.Now.AddDays(-retentionDays).ToString("o"));

        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("[Audit] Pruned {Count} entries older than {Days} days", deleted, retentionDays);
    }

    public void Dispose() => _connection.Dispose();
}
