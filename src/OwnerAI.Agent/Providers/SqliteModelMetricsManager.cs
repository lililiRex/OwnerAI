using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Providers;

/// <summary>
/// SQLite 持久化的模型调用度量管理器 — 记录每次模型调用的性能数据，用于自适应路由
/// </summary>
public sealed class SqliteModelMetricsManager : IModelMetricsManager, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger<SqliteModelMetricsManager> _logger;

    public SqliteModelMetricsManager(ILogger<SqliteModelMetricsManager> logger)
    {
        _logger = logger;

        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwnerAI");
        Directory.CreateDirectory(dbDir);

        var dbPath = Path.Combine(dbDir, "model_metrics.db");
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS model_call_metrics (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                provider_name TEXT NOT NULL,
                work_category TEXT NOT NULL,
                latency_ms REAL NOT NULL DEFAULT 0,
                success INTEGER NOT NULL DEFAULT 1,
                token_count INTEGER NOT NULL DEFAULT 0,
                timestamp TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_mcm_provider ON model_call_metrics(provider_name);
            CREATE INDEX IF NOT EXISTS idx_mcm_category ON model_call_metrics(work_category);
            CREATE INDEX IF NOT EXISTS idx_mcm_timestamp ON model_call_metrics(timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task RecordCallAsync(ModelCallMetric metric, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_call_metrics (provider_name, work_category, latency_ms, success, token_count, timestamp)
            VALUES (@provider, @category, @latency, @success, @tokens, @ts)
            """;
        cmd.Parameters.AddWithValue("@provider", metric.ProviderName);
        cmd.Parameters.AddWithValue("@category", metric.WorkCategory);
        cmd.Parameters.AddWithValue("@latency", metric.LatencyMs);
        cmd.Parameters.AddWithValue("@success", metric.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@tokens", metric.TokenCount);
        cmd.Parameters.AddWithValue("@ts", metric.Timestamp.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);

        // 定期清理旧数据（保留最近 30 天）
        await CleanupOldMetricsAsync(ct);
    }

    public async Task<ModelPerformanceSummary> GetPerformanceSummaryAsync(string providerName, string workCategory, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) AS total_calls,
                SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS success_count,
                AVG(latency_ms) AS avg_latency,
                SUM(token_count) AS total_tokens
            FROM model_call_metrics
            WHERE provider_name = @provider AND work_category = @category
            AND timestamp > @since
            """;
        cmd.Parameters.AddWithValue("@provider", providerName);
        cmd.Parameters.AddWithValue("@category", workCategory);
        cmd.Parameters.AddWithValue("@since", DateTimeOffset.Now.AddDays(-30).ToString("o"));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new ModelPerformanceSummary { ProviderName = providerName, WorkCategory = workCategory };

        return new ModelPerformanceSummary
        {
            ProviderName = providerName,
            WorkCategory = workCategory,
            TotalCalls = reader.GetInt32(0),
            SuccessCount = reader.GetInt32(1),
            AvgLatencyMs = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
            TotalTokens = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
        };
    }

    public async Task<IReadOnlyList<ModelPerformanceSummary>> GetRankingAsync(string workCategory, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT
                provider_name,
                COUNT(*) AS total_calls,
                SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS success_count,
                AVG(latency_ms) AS avg_latency,
                SUM(token_count) AS total_tokens
            FROM model_call_metrics
            WHERE work_category = @category
            AND timestamp > @since
            GROUP BY provider_name
            ORDER BY (CAST(SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS REAL) / COUNT(*)) DESC, AVG(latency_ms) ASC
            """;
        cmd.Parameters.AddWithValue("@category", workCategory);
        cmd.Parameters.AddWithValue("@since", DateTimeOffset.Now.AddDays(-30).ToString("o"));

        var results = new List<ModelPerformanceSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ModelPerformanceSummary
            {
                ProviderName = reader.GetString(0),
                WorkCategory = workCategory,
                TotalCalls = reader.GetInt32(1),
                SuccessCount = reader.GetInt32(2),
                AvgLatencyMs = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                TotalTokens = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<ModelPerformanceSummary>> GetAllSummariesAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT
                provider_name,
                work_category,
                COUNT(*) AS total_calls,
                SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS success_count,
                AVG(latency_ms) AS avg_latency,
                SUM(token_count) AS total_tokens
            FROM model_call_metrics
            WHERE timestamp > @since
            GROUP BY provider_name, work_category
            ORDER BY provider_name, work_category
            """;
        cmd.Parameters.AddWithValue("@since", DateTimeOffset.Now.AddDays(-30).ToString("o"));

        var results = new List<ModelPerformanceSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ModelPerformanceSummary
            {
                ProviderName = reader.GetString(0),
                WorkCategory = reader.GetString(1),
                TotalCalls = reader.GetInt32(2),
                SuccessCount = reader.GetInt32(3),
                AvgLatencyMs = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                TotalTokens = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            });
        }
        return results;
    }

    private async Task CleanupOldMetricsAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM model_call_metrics
            WHERE timestamp < @cutoff
            AND (SELECT COUNT(*) FROM model_call_metrics) > 10000
            """;
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.Now.AddDays(-30).ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose() => _db.Dispose();
}
