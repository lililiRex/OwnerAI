using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Scheduler;

/// <summary>
/// SQLite 持久化的计划任务管理器 — 任务跨重启存活
/// </summary>
public sealed class SqliteScheduledTaskManager : IScheduledTaskManager, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger<SqliteScheduledTaskManager> _logger;

    public SqliteScheduledTaskManager(ILogger<SqliteScheduledTaskManager> logger)
    {
        _logger = logger;

        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwnerAI");
        Directory.CreateDirectory(dbDir);

        var dbPath = Path.Combine(dbDir, "scheduler.db");
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitializeSchema();
        RecoverInterruptedTasks();
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scheduled_tasks (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT,
                type INTEGER NOT NULL DEFAULT 0,
                status INTEGER NOT NULL DEFAULT 0,
                message_template TEXT NOT NULL,
                persona TEXT,
                temperature REAL,
                priority INTEGER NOT NULL DEFAULT 3,
                scheduled_at TEXT NOT NULL,
                interval_ticks INTEGER,
                next_run_at TEXT,
                last_run_at TEXT,
                run_count INTEGER NOT NULL DEFAULT 0,
                max_retries INTEGER NOT NULL DEFAULT 3,
                consecutive_failures INTEGER NOT NULL DEFAULT 0,
                last_result TEXT,
                source TEXT NOT NULL DEFAULT 'user',
                cron_expression TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tasks_status ON scheduled_tasks(status);
            CREATE INDEX IF NOT EXISTS idx_tasks_next_run ON scheduled_tasks(next_run_at);
            CREATE INDEX IF NOT EXISTS idx_tasks_priority ON scheduled_tasks(priority DESC);
            CREATE INDEX IF NOT EXISTS idx_tasks_source ON scheduled_tasks(source);

            CREATE TABLE IF NOT EXISTS task_execution_history (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                task_name TEXT NOT NULL,
                success INTEGER NOT NULL DEFAULT 0,
                summary TEXT,
                primary_failure_summary TEXT,
                tool_overview TEXT,
                full_log TEXT,
                tool_call_count INTEGER NOT NULL DEFAULT 0,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                executed_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_history_task_id ON task_execution_history(task_id);
            CREATE INDEX IF NOT EXISTS idx_history_executed_at ON task_execution_history(executed_at DESC);
            """;
        cmd.ExecuteNonQuery();

        // 迁移: 为已有数据库添加 cron_expression 列
        MigrateAddColumn("scheduled_tasks", "cron_expression", "TEXT");

        // 迁移: 为执行历史添加 full_log 列
        MigrateAddColumn("task_execution_history", "full_log", "TEXT");
        MigrateAddColumn("task_execution_history", "primary_failure_summary", "TEXT");
        MigrateAddColumn("task_execution_history", "tool_overview", "TEXT");
    }

    private void MigrateAddColumn(string table, string column, string type)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }
    }

    /// <summary>
    /// 启动时恢复被中断的任务 (Running → Pending)
    /// </summary>
    private void RecoverInterruptedTasks()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduled_tasks
            SET status = @pending, updated_at = @now
            WHERE status IN (@running, @dispatching)
            """;
        cmd.Parameters.AddWithValue("@pending", (int)ScheduledTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@running", (int)ScheduledTaskStatus.Running);
        cmd.Parameters.AddWithValue("@dispatching", (int)ScheduledTaskStatus.Dispatching);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));
        var count = cmd.ExecuteNonQuery();
        if (count > 0)
            _logger.LogInformation("[Scheduler] Recovered {Count} interrupted tasks", count);
    }

    public Task<bool> TryMarkDispatchingAsync(string id, string? lastResult, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduled_tasks
            SET status = @dispatching,
                updated_at = @now,
                last_result = COALESCE(@result, last_result)
            WHERE id = @id
              AND status IN (@pending, @waiting_llm, @retry_waiting)
            """;
        cmd.Parameters.AddWithValue("@dispatching", (int)ScheduledTaskStatus.Dispatching);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@result", (object?)lastResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@pending", (int)ScheduledTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@waiting_llm", (int)ScheduledTaskStatus.WaitingForLlm);
        cmd.Parameters.AddWithValue("@retry_waiting", (int)ScheduledTaskStatus.RetryWaiting);

        var updated = cmd.ExecuteNonQuery() > 0;
        return Task.FromResult(updated);
    }

    public Task<string> CreateTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scheduled_tasks
            (id, name, description, type, status, message_template, persona, temperature,
             priority, scheduled_at, interval_ticks, next_run_at, last_run_at,
             run_count, max_retries, consecutive_failures, last_result, source, cron_expression, created_at, updated_at)
            VALUES
            (@id, @name, @desc, @type, @status, @msg, @persona, @temp,
             @priority, @scheduled, @interval, @next_run, @last_run,
             @run_count, @max_retries, @failures, @last_result, @source, @cron, @created, @updated)
            """;

        var now = DateTimeOffset.Now;
        cmd.Parameters.AddWithValue("@id", task.Id);
        cmd.Parameters.AddWithValue("@name", task.Name);
        cmd.Parameters.AddWithValue("@desc", (object?)task.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", (int)task.Type);
        cmd.Parameters.AddWithValue("@status", (int)task.Status);
        cmd.Parameters.AddWithValue("@msg", task.MessageTemplate);
        cmd.Parameters.AddWithValue("@persona", (object?)task.Persona ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@temp", task.Temperature.HasValue ? task.Temperature.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@priority", task.Priority);
        cmd.Parameters.AddWithValue("@scheduled", task.ScheduledAt.ToString("O"));
        cmd.Parameters.AddWithValue("@interval", task.Interval?.Ticks ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@next_run", task.NextRunAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@last_run", task.LastRunAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@run_count", task.RunCount);
        cmd.Parameters.AddWithValue("@max_retries", task.MaxRetries);
        cmd.Parameters.AddWithValue("@failures", task.ConsecutiveFailures);
        cmd.Parameters.AddWithValue("@last_result", (object?)task.LastResult ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source", task.Source);
        cmd.Parameters.AddWithValue("@cron", (object?)task.CronExpression ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", now.ToString("O"));
        cmd.Parameters.AddWithValue("@updated", now.ToString("O"));

        cmd.ExecuteNonQuery();
        _logger.LogInformation("[Scheduler] Task created: {Id} — {Name}", task.Id, task.Name);
        return Task.FromResult(task.Id);
    }

    public Task<ScheduledTask?> GetTaskAsync(string id, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM scheduled_tasks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadTask(reader) : null);
    }

    public Task<ScheduledTask?> GetNextReadyTaskAsync(CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM scheduled_tasks
            WHERE status IN (@pending, @waiting_llm, @retry_waiting)
              AND (next_run_at IS NULL OR next_run_at <= @now)
            ORDER BY priority DESC, next_run_at ASC, created_at ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@pending", (int)ScheduledTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@waiting_llm", (int)ScheduledTaskStatus.WaitingForLlm);
        cmd.Parameters.AddWithValue("@retry_waiting", (int)ScheduledTaskStatus.RetryWaiting);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));

        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadTask(reader) : null);
    }

    public Task<ScheduledTask?> GetNextReadyTaskBySourceAsync(string source, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM scheduled_tasks
            WHERE status IN (@pending, @waiting_llm, @retry_waiting)
              AND source = @source
              AND (next_run_at IS NULL OR next_run_at <= @now)
            ORDER BY priority DESC, next_run_at ASC, created_at ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@pending", (int)ScheduledTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@waiting_llm", (int)ScheduledTaskStatus.WaitingForLlm);
        cmd.Parameters.AddWithValue("@retry_waiting", (int)ScheduledTaskStatus.RetryWaiting);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));

        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadTask(reader) : null);
    }

    public Task<ScheduledTask?> GetNextReadyTaskExcludingSourceAsync(string excludeSource, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM scheduled_tasks
            WHERE status IN (@pending, @waiting_llm, @retry_waiting)
              AND source != @excludeSource
              AND (next_run_at IS NULL OR next_run_at <= @now)
            ORDER BY priority DESC, next_run_at ASC, created_at ASC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@pending", (int)ScheduledTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@waiting_llm", (int)ScheduledTaskStatus.WaitingForLlm);
        cmd.Parameters.AddWithValue("@retry_waiting", (int)ScheduledTaskStatus.RetryWaiting);
        cmd.Parameters.AddWithValue("@excludeSource", excludeSource);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));

        using var reader = cmd.ExecuteReader();
        return Task.FromResult(reader.Read() ? ReadTask(reader) : null);
    }

    public Task<IReadOnlyList<ScheduledTask>> ListTasksAsync(ScheduledTaskStatus? status, string? source, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        var conditions = new List<string>();

        if (status.HasValue)
        {
            conditions.Add("status = @status");
            cmd.Parameters.AddWithValue("@status", (int)status.Value);
        }

        if (source is not null)
        {
            conditions.Add("source = @source");
            cmd.Parameters.AddWithValue("@source", source);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"SELECT * FROM scheduled_tasks {where} ORDER BY priority DESC, created_at DESC";

        var tasks = new List<ScheduledTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tasks.Add(ReadTask(reader));

        return Task.FromResult<IReadOnlyList<ScheduledTask>>(tasks);
    }

    public Task UpdateTaskAsync(string id, ScheduledTaskStatus status,
        string? lastResult, DateTimeOffset? nextRunAt, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        var sets = new List<string>
        {
            "status = @status",
            "updated_at = @now",
        };
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);

        if (lastResult is not null)
        {
            sets.Add("last_result = @result");
            cmd.Parameters.AddWithValue("@result", lastResult);
        }

        if (nextRunAt.HasValue)
        {
            sets.Add("next_run_at = @next");
            cmd.Parameters.AddWithValue("@next", nextRunAt.Value.ToString("O"));
        }

        // 根据状态更新附加字段
        switch (status)
        {
            case ScheduledTaskStatus.Dispatching:
                break;
            case ScheduledTaskStatus.Running:
                sets.Add("last_run_at = @now");
                sets.Add("run_count = run_count + 1");
                break;
            case ScheduledTaskStatus.Failed:
                sets.Add("consecutive_failures = consecutive_failures + 1");
                break;
            case ScheduledTaskStatus.Completed:
            case ScheduledTaskStatus.Pending: // reset on recurring re-queue
            case ScheduledTaskStatus.WaitingForLlm:
            case ScheduledTaskStatus.RetryWaiting:
            case ScheduledTaskStatus.Blocked:
                sets.Add("consecutive_failures = 0");
                break;
        }

        cmd.CommandText = $"UPDATE scheduled_tasks SET {string.Join(", ", sets)} WHERE id = @id";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task CancelTaskAsync(string id, CancellationToken ct)
        => UpdateTaskAsync(id, ScheduledTaskStatus.Cancelled, lastResult: "已取消", nextRunAt: null, ct);

    public Task PauseTaskAsync(string id, CancellationToken ct)
        => UpdateTaskAsync(id, ScheduledTaskStatus.Paused, lastResult: null, nextRunAt: null, ct);

    public Task ResumeTaskAsync(string id, CancellationToken ct)
        => UpdateTaskAsync(id, ScheduledTaskStatus.Pending, lastResult: null, nextRunAt: DateTimeOffset.Now, ct);

    public Task EditTaskAsync(string id, string name, string? description, ScheduledTaskType type,
        string messageTemplate, int priority, TimeSpan? interval, string? cronExpression, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduled_tasks
            SET name = @name,
                description = @desc,
                type = @type,
                message_template = @msg,
                priority = @priority,
                interval_ticks = @interval,
                cron_expression = @cron,
                updated_at = @now
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@msg", messageTemplate);
        cmd.Parameters.AddWithValue("@priority", priority);
        cmd.Parameters.AddWithValue("@interval", interval?.Ticks ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@cron", (object?)cronExpression ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
        _logger.LogInformation("[Scheduler] Task edited: {Id} — {Name}", id, name);
        return Task.CompletedTask;
    }

    public Task DeleteTaskAsync(string id, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM scheduled_tasks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        _logger.LogInformation("[Scheduler] Task deleted: {Id}", id);
        return Task.CompletedTask;
    }

    public Task<SchedulerStats> GetStatsAsync(CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN status = 0 THEN 1 ELSE 0 END) as pending,
                SUM(CASE WHEN status = 6 THEN 1 ELSE 0 END) as waiting_llm,
                SUM(CASE WHEN status = 7 THEN 1 ELSE 0 END) as retry_waiting,
                SUM(CASE WHEN status = 8 THEN 1 ELSE 0 END) as dispatching,
                SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END) as running,
                SUM(CASE WHEN status = 2 THEN 1 ELSE 0 END) as completed,
                SUM(CASE WHEN status = 3 THEN 1 ELSE 0 END) as failed,
                SUM(CASE WHEN status = 9 THEN 1 ELSE 0 END) as blocked,
                SUM(CASE WHEN status = 5 THEN 1 ELSE 0 END) as paused,
                SUM(CASE WHEN type = 1 AND status IN (0,1,6,7,8) THEN 1 ELSE 0 END) as recurring_active,
                MIN(CASE WHEN status IN (0,6,7) AND next_run_at IS NOT NULL THEN next_run_at END) as next_run
            FROM scheduled_tasks
            """;

        using var reader = cmd.ExecuteReader();
        reader.Read();

        var nextRunStr = reader.IsDBNull(11) ? null : reader.GetString(11);
        DateTimeOffset? nextRun = nextRunStr is not null ? DateTimeOffset.Parse(nextRunStr) : null;

        return Task.FromResult(new SchedulerStats
        {
            TotalTasks = reader.GetInt32(0),
            Pending = reader.GetInt32(1),
            WaitingForLlm = reader.GetInt32(2),
            RetryWaiting = reader.GetInt32(3),
            Dispatching = reader.GetInt32(4),
            Running = reader.GetInt32(5),
            Completed = reader.GetInt32(6),
            Failed = reader.GetInt32(7),
            Blocked = reader.GetInt32(8),
            Paused = reader.GetInt32(9),
            RecurringActive = reader.GetInt32(10),
            NextScheduledRun = nextRun,
        });
    }

    public Task<bool> HasActiveTaskAsync(string name, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM scheduled_tasks
            WHERE name = @name AND status IN (@pending, @running, @paused, @waiting_llm, @retry_waiting, @dispatching, @blocked)
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@pending", (int)ScheduledTaskStatus.Pending);
        cmd.Parameters.AddWithValue("@running", (int)ScheduledTaskStatus.Running);
        cmd.Parameters.AddWithValue("@paused", (int)ScheduledTaskStatus.Paused);
        cmd.Parameters.AddWithValue("@waiting_llm", (int)ScheduledTaskStatus.WaitingForLlm);
        cmd.Parameters.AddWithValue("@retry_waiting", (int)ScheduledTaskStatus.RetryWaiting);
        cmd.Parameters.AddWithValue("@dispatching", (int)ScheduledTaskStatus.Dispatching);
        cmd.Parameters.AddWithValue("@blocked", (int)ScheduledTaskStatus.Blocked);

        var count = Convert.ToInt32(cmd.ExecuteScalar());
        return Task.FromResult(count > 0);
    }

    public Task<string> EnsureBuiltInTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        // 查找同名活跃任务
        using var findCmd = _db.CreateCommand();
        findCmd.CommandText = """
            SELECT id FROM scheduled_tasks
            WHERE name = @name AND status IN (@pending, @running, @paused, @waiting_llm, @retry_waiting, @dispatching, @blocked)
            LIMIT 1
            """;
        findCmd.Parameters.AddWithValue("@name", task.Name);
        findCmd.Parameters.AddWithValue("@pending", (int)ScheduledTaskStatus.Pending);
        findCmd.Parameters.AddWithValue("@running", (int)ScheduledTaskStatus.Running);
        findCmd.Parameters.AddWithValue("@paused", (int)ScheduledTaskStatus.Paused);
        findCmd.Parameters.AddWithValue("@waiting_llm", (int)ScheduledTaskStatus.WaitingForLlm);
        findCmd.Parameters.AddWithValue("@retry_waiting", (int)ScheduledTaskStatus.RetryWaiting);
        findCmd.Parameters.AddWithValue("@dispatching", (int)ScheduledTaskStatus.Dispatching);
        findCmd.Parameters.AddWithValue("@blocked", (int)ScheduledTaskStatus.Blocked);

        var existingId = findCmd.ExecuteScalar() as string;

        if (existingId is not null)
        {
            // 更新已有任务的 source、message_template、persona、temperature
            using var updateCmd = _db.CreateCommand();
            updateCmd.CommandText = """
                UPDATE scheduled_tasks
                SET source = @source,
                    message_template = @msg,
                    persona = @persona,
                    temperature = @temp,
                    updated_at = @now
                WHERE id = @id
                """;
            updateCmd.Parameters.AddWithValue("@id", existingId);
            updateCmd.Parameters.AddWithValue("@source", task.Source);
            updateCmd.Parameters.AddWithValue("@msg", task.MessageTemplate);
            updateCmd.Parameters.AddWithValue("@persona", (object?)task.Persona ?? DBNull.Value);
            updateCmd.Parameters.AddWithValue("@temp", task.Temperature.HasValue ? task.Temperature.Value : DBNull.Value);
            updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));
            updateCmd.ExecuteNonQuery();

            _logger.LogInformation("[Scheduler] Built-in task updated: {Id} — {Name} (source={Source})",
                existingId, task.Name, task.Source);
            return Task.FromResult(existingId);
        }

        // 不存在 → 创建新任务
        return CreateTaskAsync(task, ct);
    }

    private static ScheduledTask ReadTask(SqliteDataReader reader)
    {
        string? cronExpr = null;
        try { var ord = reader.GetOrdinal("cron_expression"); cronExpr = reader.IsDBNull(ord) ? null : reader.GetString(ord); }
        catch (ArgumentOutOfRangeException) { /* column may not exist in older DBs */ }

        return new ScheduledTask
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            Type = (ScheduledTaskType)reader.GetInt32(reader.GetOrdinal("type")),
            Status = (ScheduledTaskStatus)reader.GetInt32(reader.GetOrdinal("status")),
            MessageTemplate = reader.GetString(reader.GetOrdinal("message_template")),
            Persona = reader.IsDBNull(reader.GetOrdinal("persona")) ? null : reader.GetString(reader.GetOrdinal("persona")),
            Temperature = reader.IsDBNull(reader.GetOrdinal("temperature")) ? null : (float)reader.GetDouble(reader.GetOrdinal("temperature")),
            Priority = reader.GetInt32(reader.GetOrdinal("priority")),
            ScheduledAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("scheduled_at"))),
            Interval = reader.IsDBNull(reader.GetOrdinal("interval_ticks")) ? null : TimeSpan.FromTicks(reader.GetInt64(reader.GetOrdinal("interval_ticks"))),
            NextRunAt = reader.IsDBNull(reader.GetOrdinal("next_run_at")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("next_run_at"))),
            LastRunAt = reader.IsDBNull(reader.GetOrdinal("last_run_at")) ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_run_at"))),
            RunCount = reader.GetInt32(reader.GetOrdinal("run_count")),
            MaxRetries = reader.GetInt32(reader.GetOrdinal("max_retries")),
            ConsecutiveFailures = reader.GetInt32(reader.GetOrdinal("consecutive_failures")),
            LastResult = reader.IsDBNull(reader.GetOrdinal("last_result")) ? null : reader.GetString(reader.GetOrdinal("last_result")),
            Source = reader.GetString(reader.GetOrdinal("source")),
            CronExpression = cronExpr,
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        };
    }

    public Task RecordExecutionAsync(TaskExecutionRecord record, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task_execution_history
            (id, task_id, task_name, success, summary, primary_failure_summary, tool_overview, full_log, tool_call_count, duration_ms, executed_at)
            VALUES (@id, @task_id, @task_name, @success, @summary, @primary_failure_summary, @tool_overview, @full_log, @tool_calls, @duration, @executed)
            """;
        cmd.Parameters.AddWithValue("@id", record.Id);
        cmd.Parameters.AddWithValue("@task_id", record.TaskId);
        cmd.Parameters.AddWithValue("@task_name", record.TaskName);
        cmd.Parameters.AddWithValue("@success", record.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@summary", (object?)record.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@primary_failure_summary", (object?)record.PrimaryFailureSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tool_overview", (object?)record.ToolOverview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@full_log", (object?)record.FullLog ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tool_calls", record.ToolCallCount);
        cmd.Parameters.AddWithValue("@duration", (long)record.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("@executed", record.ExecutedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskExecutionRecord>> GetExecutionHistoryAsync(string? taskId, int limit, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        var where = taskId is not null ? "WHERE task_id = @task_id" : "";
        cmd.CommandText = $"SELECT * FROM task_execution_history {where} ORDER BY executed_at DESC LIMIT @limit";
        if (taskId is not null)
            cmd.Parameters.AddWithValue("@task_id", taskId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var records = new List<TaskExecutionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string? primaryFailureSummary = null;
            string? toolOverview = null;
            string? fullLog = null;
            try { var ord = reader.GetOrdinal("primary_failure_summary"); primaryFailureSummary = reader.IsDBNull(ord) ? null : reader.GetString(ord); }
            catch (ArgumentOutOfRangeException) { /* column may not exist in older DBs */ }
            try { var ord = reader.GetOrdinal("tool_overview"); toolOverview = reader.IsDBNull(ord) ? null : reader.GetString(ord); }
            catch (ArgumentOutOfRangeException) { /* column may not exist in older DBs */ }
            try { var ord = reader.GetOrdinal("full_log"); fullLog = reader.IsDBNull(ord) ? null : reader.GetString(ord); }
            catch (ArgumentOutOfRangeException) { /* column may not exist in older DBs */ }

            records.Add(new TaskExecutionRecord
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                TaskId = reader.GetString(reader.GetOrdinal("task_id")),
                TaskName = reader.GetString(reader.GetOrdinal("task_name")),
                Success = reader.GetInt32(reader.GetOrdinal("success")) == 1,
                Summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary")),
                PrimaryFailureSummary = primaryFailureSummary,
                ToolOverview = toolOverview,
                FullLog = fullLog,
                ToolCallCount = reader.GetInt32(reader.GetOrdinal("tool_call_count")),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(reader.GetOrdinal("duration_ms"))),
                ExecutedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("executed_at"))),
            });
        }
        return Task.FromResult<IReadOnlyList<TaskExecutionRecord>>(records);
    }

    public void Dispose() => _db.Dispose();
}
