using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Evolution;

/// <summary>
/// SQLite 持久化的自我进化管理器 — 跟踪能力缺口与进化历史
/// </summary>
public sealed class SqliteEvolutionManager : IEvolutionManager, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILogger<SqliteEvolutionManager> _logger;
    private readonly IEventBus? _eventBus;

    public SqliteEvolutionManager(ILogger<SqliteEvolutionManager> logger, IEventBus? eventBus = null)
    {
        _logger = logger;
        _eventBus = eventBus;

        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwnerAI");
        Directory.CreateDirectory(dbDir);

        var dbPath = Path.Combine(dbDir, "evolution.db");
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS evolution_gaps (
                id TEXT PRIMARY KEY,
                description TEXT NOT NULL,
                source TEXT NOT NULL DEFAULT 'self_analysis',
                category TEXT NOT NULL DEFAULT 'source',
                status INTEGER NOT NULL DEFAULT 0,
                priority INTEGER NOT NULL DEFAULT 3,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                resolved_at TEXT,
                resolution TEXT,
                last_attempt_log TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_gaps_status ON evolution_gaps(status);
            CREATE INDEX IF NOT EXISTS idx_gaps_priority ON evolution_gaps(priority DESC);

            CREATE TABLE IF NOT EXISTS plan_steps (
                id TEXT PRIMARY KEY,
                gap_id TEXT NOT NULL,
                parent_step_id TEXT,
                title TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                hypothesis TEXT NOT NULL DEFAULT '',
                acceptance_criteria TEXT NOT NULL DEFAULT '',
                verification_script TEXT,
                verification_exit_code INTEGER,
                verification_output TEXT,
                step_type INTEGER NOT NULL DEFAULT 1,
                step_order INTEGER NOT NULL DEFAULT 0,
                depth INTEGER NOT NULL DEFAULT 0,
                status INTEGER NOT NULL DEFAULT 0,
                checkpoint TEXT,
                result TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (gap_id) REFERENCES evolution_gaps(id)
            );
            CREATE INDEX IF NOT EXISTS idx_steps_gap ON plan_steps(gap_id);
            CREATE INDEX IF NOT EXISTS idx_steps_parent ON plan_steps(parent_step_id);

            CREATE TABLE IF NOT EXISTS evolution_metrics (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                gap_id TEXT NOT NULL,
                metric_type TEXT NOT NULL,
                value REAL NOT NULL DEFAULT 0,
                detail TEXT,
                timestamp TEXT NOT NULL,
                FOREIGN KEY (gap_id) REFERENCES evolution_gaps(id)
            );
            CREATE INDEX IF NOT EXISTS idx_metrics_gap ON evolution_metrics(gap_id);
            CREATE INDEX IF NOT EXISTS idx_metrics_type ON evolution_metrics(metric_type);
            """;
        cmd.ExecuteNonQuery();

        // Migration: add columns for existing databases
        MigrateAddColumn("category", "TEXT NOT NULL DEFAULT 'source'");
        MigrateAddColumnToTable("plan_steps", "hypothesis", "TEXT NOT NULL DEFAULT ''");
        MigrateAddColumnToTable("plan_steps", "acceptance_criteria", "TEXT NOT NULL DEFAULT ''");
        MigrateAddColumnToTable("plan_steps", "step_type", "INTEGER NOT NULL DEFAULT 1");
        MigrateAddColumnToTable("plan_steps", "verification_script", "TEXT");
        MigrateAddColumnToTable("plan_steps", "verification_exit_code", "INTEGER");
        MigrateAddColumnToTable("plan_steps", "verification_output", "TEXT");
        MigrateAddColumnToTable("plan_steps", "checkpoint", "TEXT");
    }

    private void MigrateAddColumn(string column, string definition)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"ALTER TABLE evolution_gaps ADD COLUMN {column} {definition}";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }
    }

    private void MigrateAddColumnToTable(string table, string column, string definition)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }
    }

    public async Task<string> ReportGapAsync(string description, string source, int priority, string category, CancellationToken ct)
    {
        // 检查是否已有相似缺口
        if (await HasSimilarGapAsync(description, ct))
        {
            _logger.LogInformation("[Evolution] Similar gap already exists, skipping: {Desc}", Truncate(description, 80));
            return string.Empty;
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.Now.ToString("o");

        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO evolution_gaps (id, description, source, category, status, priority, attempt_count, created_at, updated_at)
            VALUES (@id, @desc, @source, @category, 0, @priority, 0, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@desc", description);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@category", category is "source" or "skill" ? category : "source");
        cmd.Parameters.AddWithValue("@priority", Math.Clamp(priority, 1, 5));
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[Evolution] Gap reported: {Id} — {Desc}", id, Truncate(description, 80));
        await PublishChangeEventAsync("新缺口已报告", true, id, description, ct);
        return id;
    }

    public async Task<IReadOnlyList<EvolutionGap>> ListGapsAsync(EvolutionGapStatus? status, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = status.HasValue
            ? "SELECT * FROM evolution_gaps WHERE status = @status ORDER BY priority DESC, created_at ASC"
            : "SELECT * FROM evolution_gaps ORDER BY priority DESC, created_at ASC";

        if (status.HasValue)
            cmd.Parameters.AddWithValue("@status", (int)status.Value);

        var gaps = new List<EvolutionGap>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            gaps.Add(ReadGap(reader));
        }
        return gaps;
    }

    public async Task UpdateGapAsync(string id, EvolutionGapStatus status, string? resolution, string? attemptLog, CancellationToken ct)
        => await SetGapStatusAsync(id, status, resolution, attemptLog, incrementAttemptCount: true, ct);

    private async Task SetGapStatusAsync(
        string id,
        EvolutionGapStatus status,
        string? resolution,
        string? attemptLog,
        bool incrementAttemptCount,
        CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        var now = DateTimeOffset.Now.ToString("o");

        cmd.CommandText = """
            UPDATE evolution_gaps
            SET status = @status,
                updated_at = @now,
                attempt_count = attempt_count + @attempt_increment,
                resolved_at = CASE WHEN @status = 4 THEN @now ELSE resolved_at END,
                resolution = COALESCE(@resolution, resolution),
                last_attempt_log = COALESCE(@log, last_attempt_log)
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@attempt_increment", incrementAttemptCount ? 1 : 0);
        cmd.Parameters.AddWithValue("@resolution", (object?)resolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@log", (object?)attemptLog ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[Evolution] Gap {Id} updated to {Status}", id, status);
        var phase = status switch
        {
            EvolutionGapStatus.Planning => "规划中",
            EvolutionGapStatus.Implementing => "实现中",
            EvolutionGapStatus.Verifying => "验证中",
            EvolutionGapStatus.Resolved => "已解决",
            EvolutionGapStatus.Failed => "失败",
            _ => "状态变更",
        };
        var isActive = status is EvolutionGapStatus.Planning
            or EvolutionGapStatus.Implementing
            or EvolutionGapStatus.Verifying;
        await PublishChangeEventAsync(phase, isActive, id, resolution, ct);
    }

    public async Task<EvolutionGap?> GetNextPendingGapAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM evolution_gaps
            WHERE status IN (0, 5)
            AND attempt_count < 3
            ORDER BY priority DESC, created_at ASC
            LIMIT 1
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadGap(reader) : null;
    }

    public async Task<EvolutionStats> GetStatsAsync(CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN status = 4 THEN 1 ELSE 0 END) AS resolved,
                SUM(CASE WHEN status = 5 THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN status IN (0, 6) THEN 1 ELSE 0 END) AS pending,
                SUM(CASE WHEN status IN (1, 2, 3) THEN 1 ELSE 0 END) AS in_progress,
                MAX(CASE WHEN status = 4 THEN resolved_at END) AS last_resolved
            FROM evolution_gaps
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new EvolutionStats();

        return new EvolutionStats
        {
            TotalGaps = reader.GetInt32(0),
            Resolved = reader.GetInt32(1),
            Failed = reader.GetInt32(2),
            Pending = reader.GetInt32(3),
            InProgress = reader.GetInt32(4),
            LastEvolutionAt = reader.IsDBNull(5)
                ? null
                : DateTimeOffset.Parse(reader.GetString(5)),
        };
    }

    public async Task<bool> HasSimilarGapAsync(string description, CancellationToken ct)
    {
        // 简单的关键词匹配 — 检查是否有未解决的相似缺口
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM evolution_gaps
            WHERE status NOT IN (4, 6)
            AND (description LIKE '%' || @keyword || '%' OR @keyword LIKE '%' || description || '%')
            """;

        // 提取核心关键词（取描述前30个字符）
        var keyword = description.Length > 30 ? description[..30] : description;
        cmd.Parameters.AddWithValue("@keyword", keyword);

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public async Task<EvolutionGap?> GetNextGapForPlanningAsync(CancellationToken ct)
    {
        var gaps = await ListGapsAsync(status: null, ct: ct);
        foreach (var gap in gaps.Where(g => g.Status is EvolutionGapStatus.Detected
                                                 or EvolutionGapStatus.Deferred
                                                 or EvolutionGapStatus.Failed
                                                 or EvolutionGapStatus.Planning))
        {
            if (!await HasPlanAsync(gap.Id, ct))
                return gap;
        }

        return null;
    }

    public async Task<EvolutionGap?> GetNextGapForImplementationAsync(CancellationToken ct)
    {
        var gaps = await ListGapsAsync(status: null, ct: ct);
        foreach (var gap in gaps.Where(g => g.Status is EvolutionGapStatus.Planning or EvolutionGapStatus.Implementing or EvolutionGapStatus.Failed))
        {
            if (!await HasPlanAsync(gap.Id, ct))
                continue;

            var next = await GetNextPendingStepAsync(gap.Id, ct);
            if (next is not null && next.StepType != PlanStepType.Verification)
                return gap;
        }

        return null;
    }

    public async Task<EvolutionGap?> GetNextGapForVerificationAsync(CancellationToken ct)
    {
        var gaps = await ListGapsAsync(status: null, ct: ct);
        foreach (var gap in gaps.Where(g => g.Status is EvolutionGapStatus.Verifying or EvolutionGapStatus.Implementing or EvolutionGapStatus.Planning))
        {
            if (!await HasPlanAsync(gap.Id, ct))
                continue;

            var steps = await GetPlanStepsAsync(gap.Id, ct);
            if (steps.Count == 0)
                continue;

            var hasRemainingNonVerification = steps.Any(s =>
                s.StepType != PlanStepType.Verification
                && s.Status is not PlanStepStatus.Completed and not PlanStepStatus.Skipped);

            if (hasRemainingNonVerification)
                continue;

            if (steps.Any(s => s.Status is not PlanStepStatus.Completed and not PlanStepStatus.Skipped) || gap.Status != EvolutionGapStatus.Resolved)
                return gap;
        }

        return null;
    }

    private static EvolutionGap ReadGap(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Description = reader.GetString(reader.GetOrdinal("description")),
        Source = reader.GetString(reader.GetOrdinal("source")),
        Category = reader.IsDBNull(reader.GetOrdinal("category"))
            ? "source"
            : reader.GetString(reader.GetOrdinal("category")),
        Status = (EvolutionGapStatus)reader.GetInt32(reader.GetOrdinal("status")),
        Priority = reader.GetInt32(reader.GetOrdinal("priority")),
        AttemptCount = reader.GetInt32(reader.GetOrdinal("attempt_count")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        ResolvedAt = reader.IsDBNull(reader.GetOrdinal("resolved_at"))
            ? null
            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("resolved_at"))),
        Resolution = reader.IsDBNull(reader.GetOrdinal("resolution"))
            ? null
            : reader.GetString(reader.GetOrdinal("resolution")),
        LastAttemptLog = reader.IsDBNull(reader.GetOrdinal("last_attempt_log"))
            ? null
            : reader.GetString(reader.GetOrdinal("last_attempt_log")),
    };

    // ── 树形实现计划 ──

    public async Task CreatePlanAsync(string gapId, List<PlanStepInput> steps, CancellationToken ct)
    {
        // 清除已有计划（重新规划场景）
        await using (var delCmd = _db.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM plan_steps WHERE gap_id = @gapId";
            delCmd.Parameters.AddWithValue("@gapId", gapId);
            await delCmd.ExecuteNonQueryAsync(ct);
        }

        var now = DateTimeOffset.Now.ToString("o");
        await InsertStepsRecursiveAsync(gapId, parentStepId: null, steps, depth: 0, now, ct);

        // 将缺口状态更新为 Planning
        await UpdateGapAsync(gapId, EvolutionGapStatus.Planning, null, $"已创建 {CountSteps(steps)} 步实现计划", ct);

        _logger.LogInformation("[Evolution] Plan created for gap {GapId}: {StepCount} steps",
            gapId, CountSteps(steps));
    }

    private async Task InsertStepsRecursiveAsync(
        string gapId, string? parentStepId, List<PlanStepInput> steps,
        int depth, string now, CancellationToken ct)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var id = Guid.NewGuid().ToString("N")[..12];

            var stepType = step.StepType?.ToLowerInvariant() switch
            {
                "diagnostic" or "diagnosis" or "analysis" => (int)PlanStepType.Diagnostic,
                "verification" or "verify" or "test" => (int)PlanStepType.Verification,
                _ => (int)PlanStepType.Implementation,
            };

            await using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO plan_steps (id, gap_id, parent_step_id, title, description, hypothesis, acceptance_criteria, verification_script, step_type, step_order, depth, status, created_at, updated_at)
                VALUES (@id, @gapId, @parentId, @title, @desc, @hypo, @ac, @vs, @stepType, @order, @depth, 0, @now, @now)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@gapId", gapId);
            cmd.Parameters.AddWithValue("@parentId", (object?)parentStepId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", step.Title);
            cmd.Parameters.AddWithValue("@desc", step.Description);
            cmd.Parameters.AddWithValue("@hypo", step.Hypothesis);
            cmd.Parameters.AddWithValue("@ac", step.AcceptanceCriteria);
            cmd.Parameters.AddWithValue("@vs", (object?)step.VerificationScript ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@stepType", stepType);
            cmd.Parameters.AddWithValue("@order", i);
            cmd.Parameters.AddWithValue("@depth", depth);
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync(ct);

            if (step.Children.Count > 0)
                await InsertStepsRecursiveAsync(gapId, id, step.Children, depth + 1, now, ct);
        }
    }

    private static int CountSteps(List<PlanStepInput> steps)
        => steps.Sum(s => 1 + CountSteps(s.Children));

    public async Task<IReadOnlyList<EvolutionPlanStep>> GetPlanStepsAsync(string gapId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM plan_steps
            WHERE gap_id = @gapId
            ORDER BY depth ASC, step_order ASC
            """;
        cmd.Parameters.AddWithValue("@gapId", gapId);

        var steps = new List<EvolutionPlanStep>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            steps.Add(ReadStep(reader));
        return steps;
    }

    public async Task<EvolutionPlanStep?> GetNextPendingStepAsync(string gapId, CancellationToken ct)
    {
        // 深度优先：先找最深的未完成叶子节点（从前往后）
        // 策略：找所有 Pending 步骤，优先取 depth 最大、order 最小的
        // 但前提是其父步骤不为 Pending（即父已经 InProgress 或还没开始执行子）
        // 简化逻辑：找第一个所有子步骤都已完成的 Pending 步骤，或者无子步骤的 Pending 步骤
        var allSteps = await GetPlanStepsAsync(gapId, ct);
        if (allSteps.Count == 0) return null;

        // 构建子节点映射
        var childrenMap = allSteps
            .GroupBy(s => s.ParentStepId ?? "")
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Order).ToList());

        // 深度优先遍历，找第一个可执行的叶子步骤
        return FindNextExecutable(parentId: "", childrenMap);
    }

    private static EvolutionPlanStep? FindNextExecutable(
        string parentId,
        Dictionary<string, List<EvolutionPlanStep>> childrenMap)
    {
        if (!childrenMap.TryGetValue(parentId, out var siblings))
            return null;

        foreach (var step in siblings)
        {
            // 已完成或跳过的步骤 → 跳过
            if (step.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped)
                continue;

            // 如果有子步骤 → 先递归进入子步骤
            if (childrenMap.TryGetValue(step.Id, out var childSteps))
            {
                var childResult = FindNextExecutable(step.Id, childrenMap);
                if (childResult is not null)
                    return childResult;

                // 所有子步骤都已完成 → 这个步骤本身可以被标记完成（但不自动完成，返回它让 Agent 确认）
                var allChildrenDone = childSteps
                    .All(c => c.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped);
                if (allChildrenDone && step.Status == PlanStepStatus.Pending)
                    return step;
            }

            // 叶子步骤且 Pending → 返回
            if (step.Status is PlanStepStatus.Pending or PlanStepStatus.Failed)
                return step;
        }

        return null;
    }

    public async Task UpdateStepAsync(string stepId, PlanStepStatus status, string? result, CancellationToken ct)
    {
        var now = DateTimeOffset.Now.ToString("o");
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE plan_steps
            SET status = @status,
                result = COALESCE(@result, result),
                updated_at = @now
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", stepId);
        cmd.Parameters.AddWithValue("@status", (int)status);
        cmd.Parameters.AddWithValue("@result", (object?)result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[Evolution] Step {StepId} updated to {Status}", stepId, status);
        await SyncGapStatusFromStepAsync(stepId, result, ct);
    }

    private async Task SyncGapStatusFromStepAsync(string stepId, string? result, CancellationToken ct)
    {
        await using var gapCmd = _db.CreateCommand();
        gapCmd.CommandText = "SELECT gap_id FROM plan_steps WHERE id = @id LIMIT 1";
        gapCmd.Parameters.AddWithValue("@id", stepId);
        var gapId = await gapCmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(gapId))
            return;

        var steps = await GetPlanStepsAsync(gapId, ct);
        if (steps.Count == 0)
            return;

        if (steps.Any(s => s.Status == PlanStepStatus.Failed))
        {
            await SetGapStatusAsync(gapId, EvolutionGapStatus.Failed, null,
                string.IsNullOrWhiteSpace(result) ? "实现计划存在失败步骤" : result,
                incrementAttemptCount: false, ct);
            return;
        }

        var hasRemainingNonVerification = steps.Any(s =>
            s.StepType != PlanStepType.Verification
            && s.Status is not PlanStepStatus.Completed and not PlanStepStatus.Skipped);

        var hasVerificationSteps = steps.Any(s => s.StepType == PlanStepType.Verification);

        var nextStatus = hasRemainingNonVerification
            ? EvolutionGapStatus.Implementing
            : hasVerificationSteps
                ? EvolutionGapStatus.Verifying
                : EvolutionGapStatus.Verifying;

        var log = nextStatus switch
        {
            EvolutionGapStatus.Implementing => "计划已进入实施阶段",
            EvolutionGapStatus.Verifying => "实现步骤已完成，进入验收验证阶段",
            _ => null,
        };

        await SetGapStatusAsync(gapId, nextStatus, null, log, incrementAttemptCount: false, ct);
    }

    public async Task<bool> HasPlanAsync(string gapId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM plan_steps WHERE gap_id = @gapId";
        cmd.Parameters.AddWithValue("@gapId", gapId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    private static EvolutionPlanStep ReadStep(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        GapId = reader.GetString(reader.GetOrdinal("gap_id")),
        ParentStepId = reader.IsDBNull(reader.GetOrdinal("parent_step_id"))
            ? null
            : reader.GetString(reader.GetOrdinal("parent_step_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Description = reader.IsDBNull(reader.GetOrdinal("description"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("description")),
        Hypothesis = reader.IsDBNull(reader.GetOrdinal("hypothesis"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("hypothesis")),
        AcceptanceCriteria = reader.IsDBNull(reader.GetOrdinal("acceptance_criteria"))
            ? string.Empty
            : reader.GetString(reader.GetOrdinal("acceptance_criteria")),
        VerificationScript = TryReadString(reader, "verification_script"),
        VerificationExitCode = TryReadNullableInt(reader, "verification_exit_code"),
        VerificationOutput = TryReadString(reader, "verification_output"),
        StepType = (PlanStepType)reader.GetInt32(reader.GetOrdinal("step_type")),
        Order = reader.GetInt32(reader.GetOrdinal("step_order")),
        Depth = reader.GetInt32(reader.GetOrdinal("depth")),
        Status = (PlanStepStatus)reader.GetInt32(reader.GetOrdinal("status")),
        Checkpoint = TryReadString(reader, "checkpoint"),
        Result = reader.IsDBNull(reader.GetOrdinal("result"))
            ? null
            : reader.GetString(reader.GetOrdinal("result")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
    };

    private static string? TryReadString(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int? TryReadNullableInt(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private async Task PublishChangeEventAsync(string phase, bool isActive, string? gapId, string? gapDescription, CancellationToken ct)
    {
        if (_eventBus is null) return;
        try
        {
            var stats = await GetStatsAsync(ct);
            await _eventBus.PublishAsync(new EvolutionStatusEvent
            {
                Phase = phase,
                IsActive = isActive,
                GapId = gapId,
                GapDescription = gapDescription,
                Stats = new EvolutionStats
                {
                    TotalGaps = stats.TotalGaps,
                    Resolved = stats.Resolved,
                    Failed = stats.Failed,
                    Pending = stats.Pending,
                    InProgress = stats.InProgress,
                    LastEvolutionAt = stats.LastEvolutionAt,
                },
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Evolution] Failed to publish change event (non-critical)");
        }
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "...");

    // ── 新增接口实现：验证、检查点、验收门、度量 ──

    public async Task UpdateStepVerificationAsync(string stepId, int exitCode, string? output, CancellationToken ct)
    {
        var now = DateTimeOffset.Now.ToString("o");
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE plan_steps
            SET verification_exit_code = @exitCode,
                verification_output = @output,
                updated_at = @now
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", stepId);
        cmd.Parameters.AddWithValue("@exitCode", exitCode);
        cmd.Parameters.AddWithValue("@output", (object?)output ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("[Evolution] Step {StepId} verification: exitCode={ExitCode}", stepId, exitCode);
    }

    public async Task SaveStepCheckpointAsync(string stepId, string checkpoint, CancellationToken ct)
    {
        var now = DateTimeOffset.Now.ToString("o");
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE plan_steps
            SET checkpoint = @checkpoint,
                updated_at = @now
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", stepId);
        cmd.Parameters.AddWithValue("@checkpoint", checkpoint);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AcceptanceGateResult> CheckAcceptanceGateAsync(string gapId, CancellationToken ct)
    {
        var steps = await GetPlanStepsAsync(gapId, ct);
        var verificationSteps = steps.Where(s => s.StepType == PlanStepType.Verification).ToList();

        if (verificationSteps.Count == 0)
        {
            // 没有 Verification 步骤时检查所有步骤是否完成
            var allDone = steps.All(s => s.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped);
            return new AcceptanceGateResult
            {
                Passed = allDone,
                Summary = allDone ? "所有步骤已完成（无自动化验证步骤）" : "仍有未完成步骤",
            };
        }

        var passed = 0;
        var failed = 0;
        var notVerified = 0;
        var failedStepNames = new List<string>();

        foreach (var step in verificationSteps)
        {
            if (step.Status is not PlanStepStatus.Completed and not PlanStepStatus.Skipped)
            {
                notVerified++;
                failedStepNames.Add($"[{step.Id}] {step.Title} (未执行)");
                continue;
            }

            if (step.VerificationScript is not null)
            {
                if (step.VerificationExitCode == 0)
                    passed++;
                else
                {
                    failed++;
                    failedStepNames.Add($"[{step.Id}] {step.Title} (exit={step.VerificationExitCode})");
                }
            }
            else
            {
                // 无脚本但已完成的验证步骤视为通过
                passed++;
            }
        }

        var gatePassed = failed == 0 && notVerified == 0;
        var sb = new System.Text.StringBuilder();
        sb.Append($"验收门: {passed}/{verificationSteps.Count} 通过");
        if (failed > 0) sb.Append($", {failed} 失败");
        if (notVerified > 0) sb.Append($", {notVerified} 未验证");

        return new AcceptanceGateResult
        {
            Passed = gatePassed,
            PassedCount = passed,
            FailedCount = failed,
            NotVerifiedCount = notVerified,
            Summary = sb.ToString(),
            FailedSteps = failedStepNames,
        };
    }

    public async Task<EvolutionPlanStep?> GetStepAsync(string stepId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT * FROM plan_steps WHERE id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", stepId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadStep(reader) : null;
    }

    public async Task RecordEvolutionMetricAsync(EvolutionMetric metric, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO evolution_metrics (gap_id, metric_type, value, detail, timestamp)
            VALUES (@gapId, @type, @value, @detail, @ts)
            """;
        cmd.Parameters.AddWithValue("@gapId", metric.GapId);
        cmd.Parameters.AddWithValue("@type", metric.MetricType);
        cmd.Parameters.AddWithValue("@value", metric.Value);
        cmd.Parameters.AddWithValue("@detail", (object?)metric.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", metric.Timestamp.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EvolutionEnhancedStats> GetEnhancedStatsAsync(CancellationToken ct)
    {
        var basic = await GetStatsAsync(ct);

        // Token cost
        double totalTokenCost = 0;
        double avgTokenPerResolved = 0;
        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(value), 0) FROM evolution_metrics WHERE metric_type = 'token_cost'";
            totalTokenCost = Convert.ToDouble(await cmd.ExecuteScalarAsync(ct));
            if (basic.Resolved > 0) avgTokenPerResolved = totalTokenCost / basic.Resolved;
        }

        // Acceptance pass rate
        double acceptancePassRate = 0;
        if (basic.Resolved + basic.Failed > 0)
            acceptancePassRate = (double)basic.Resolved / (basic.Resolved + basic.Failed);

        // Average resolution time
        var avgResolutionTime = TimeSpan.Zero;
        await using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT AVG(julianday(resolved_at) - julianday(created_at)) * 86400
                FROM evolution_gaps
                WHERE status = 4 AND resolved_at IS NOT NULL
                """;
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is not null and not DBNull)
            {
                var seconds = Convert.ToDouble(result);
                if (!double.IsNaN(seconds) && seconds > 0)
                    avgResolutionTime = TimeSpan.FromSeconds(seconds);
            }
        }

        return new EvolutionEnhancedStats
        {
            Basic = basic,
            TotalTokenCost = totalTokenCost,
            AvgTokenPerResolved = avgTokenPerResolved,
            AcceptancePassRate = acceptancePassRate,
            AvgResolutionTime = avgResolutionTime,
        };
    }

    public void Dispose() => _db.Dispose();
}
