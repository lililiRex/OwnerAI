using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Scheduler;

/// <summary>
/// 计划任务工具 — AI 可以创建、查看、取消计划任务
/// <para>支持一次性任务和循环任务。创建后由 SchedulerService 在后台调度执行。</para>
/// </summary>
[Tool("schedule_task", "计划任务工具 — 创建一次性或循环任务、查看任务列表、取消/暂停/恢复任务。用户说「每天提醒我」「定时执行」「过一小时后帮我做」等场景时使用",
    SecurityLevel = ToolSecurityLevel.Low,
    TimeoutSeconds = 15)]
public sealed class ScheduledTaskTool(
    IScheduledTaskManager taskManager,
    ILogger<ScheduledTaskTool> logger) : IOwnerAITool
{
    public bool IsAvailable(ToolContext context) => true;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement parameters,
        ToolContext context,
        CancellationToken ct)
    {
        if (!TryGetString(parameters, ["action", "operation", "op"], out var action))
            return ToolResult.Error("缺少参数: action (可选: create, list, cancel, pause, resume, status)",
                errorCode: "missing_action",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "提供 action 参数，例如 create、list、status。", retryable: false);
        action = action?.ToLowerInvariant();

        return action switch
        {
            "create" => await CreateTaskAsync(parameters, ct),
            "list" => await ListTasksAsync(parameters, ct),
            "cancel" => await CancelTaskAsync(parameters, ct),
            "pause" => await PauseTaskAsync(parameters, ct),
            "resume" => await ResumeTaskAsync(parameters, ct),
            "status" => await GetStatusAsync(ct),
            "history" => await GetHistoryAsync(parameters, ct),
            _ => ToolResult.Error($"未知操作: {action}。可选: create, list, cancel, pause, resume, status, history",
                errorCode: "invalid_action",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "将 action 设置为 create、list、cancel、pause、resume、status 或 history。", retryable: false),
        };
    }

    private async Task<ToolResult> CreateTaskAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!TryGetString(parameters, ["name", "task_name", "title"], out var name))
            return ToolResult.Error("缺少参数: name (任务名称)",
                errorCode: "missing_name",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "提供 name、task_name 或 title 字段作为任务名称。", retryable: false);

        if (!TryGetString(parameters, ["message", "prompt", "content"], out var message))
            return ToolResult.Error("缺少参数: message (发送给 Agent 执行的提示词)",
                errorCode: "missing_message",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "提供 message、prompt 或 content 字段作为任务提示词。", retryable: false);

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(message))
            return ToolResult.Error("name 和 message 不能为空",
                errorCode: "empty_required_fields",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "确保 name 和 message 都是非空字符串。", retryable: false);

        // 解析任务类型
        var typeStr = TryGetString(parameters, ["type", "task_type"], out var normalizedType)
            ? normalizedType?.ToLowerInvariant()
            : "once";
        ScheduledTaskType? taskType = typeStr switch
        {
            "recurring" => ScheduledTaskType.Recurring,
            "cron" => ScheduledTaskType.Cron,
            "once" or "one_time" or "onetime" or null or "" => ScheduledTaskType.OneTime,
            _ => null,
        };

        if (taskType is null)
            return ToolResult.Error($"未知任务类型: {typeStr}。可选: once, recurring, cron",
                errorCode: "invalid_task_type",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "将 type 或 task_type 设置为 once、recurring 或 cron。", retryable: false);

        var normalizedTaskType = taskType.Value;

        // 解析延迟/定时
        DateTimeOffset scheduledAt = DateTimeOffset.Now;
        if (TryGetInt32(parameters, ["delay_minutes", "delay", "delay_min"], out var delayMin))
        {
            if (delayMin < 0)
                return ToolResult.Error("delay_minutes 不能为负数。",
                    errorCode: "invalid_delay_minutes",
                    failureCategory: ToolFailureCategory.ValidationError,
                    suggestedFix: "提供大于等于 0 的 delay_minutes、delay 或 delay_min。", retryable: false);

            scheduledAt = DateTimeOffset.Now.AddMinutes(delayMin);
        }

        // 解析 Cron 表达式
        string? cronExpression = null;
        if (normalizedTaskType == ScheduledTaskType.Cron)
        {
            TryGetString(parameters, ["cron_expression", "cron", "cron_expr"], out cronExpression);

            if (string.IsNullOrWhiteSpace(cronExpression) || !CronHelper.IsValid(cronExpression))
                return ToolResult.Error("Cron 任务需要有效的 cron_expression 参数 (格式: \"分 时 日 月 周\"，如 \"0 9 * * *\" 表示每天 9:00)",
                    errorCode: "invalid_cron_expression",
                    failureCategory: ToolFailureCategory.ValidationError,
                    suggestedFix: "提供合法的 cron_expression、cron 或 cron_expr，例如 0 9 * * *。", retryable: false);

            var nextRun = CronHelper.GetNextOccurrence(cronExpression, DateTimeOffset.Now);
            if (nextRun.HasValue)
                scheduledAt = nextRun.Value;
        }

        // 解析循环间隔 (分钟)
        TimeSpan? interval = null;
        if (normalizedTaskType == ScheduledTaskType.Recurring)
        {
            if (TryGetInt32(parameters, ["interval_minutes", "interval", "interval_min"], out var intervalMin))
            {
                if (intervalMin <= 0)
                    return ToolResult.Error("循环任务的 interval_minutes 必须为正整数。",
                        errorCode: "invalid_interval_minutes",
                        failureCategory: ToolFailureCategory.ValidationError,
                        suggestedFix: "为 recurring 任务提供大于 0 的 interval_minutes、interval 或 interval_min。", retryable: false);

                interval = TimeSpan.FromMinutes(intervalMin);
            }
            else
            {
                return ToolResult.Error("循环任务需要 interval_minutes 参数 (循环间隔，单位: 分钟)",
                    errorCode: "missing_interval_minutes",
                    failureCategory: ToolFailureCategory.ValidationError,
                    suggestedFix: "为 recurring 任务提供 interval_minutes、interval 或 interval_min。", retryable: false);
            }
        }

        // 解析优先级
        var priority = TryGetInt32(parameters, ["priority", "prio"], out var p)
            ? Math.Clamp(p, 1, 5)
            : 3;

        // 解析描述
        TryGetString(parameters, ["description", "desc"], out var description);

        var id = Guid.NewGuid().ToString("N")[..12];

        var task = new ScheduledTask
        {
            Id = id,
            Name = name,
            Description = description,
            Type = normalizedTaskType,
            Status = ScheduledTaskStatus.Queued,
            MessageTemplate = message,
            Priority = priority,
            ScheduledAt = scheduledAt,
            Interval = interval,
            NextRunAt = scheduledAt,
            CronExpression = cronExpression,
            Source = "ai",
        };

        await taskManager.CreateTaskAsync(task, ct);
        logger.LogInformation("[ScheduledTask] Created: {Id} — {Name} ({Type})", id, name, normalizedTaskType);

        var sb = new StringBuilder();
        sb.AppendLine($"✅ 计划任务已创建");
        sb.AppendLine($"- ID: {id}");
        sb.AppendLine($"- 名称: {name}");
        sb.AppendLine($"- 类型: {normalizedTaskType switch { ScheduledTaskType.Recurring => "循环", ScheduledTaskType.Cron => "Cron 定时", _ => "一次性" }}");
        sb.AppendLine($"- 优先级: P{priority}");
        sb.AppendLine($"- 计划时间: {scheduledAt:yyyy-MM-dd HH:mm}");
        if (interval.HasValue)
            sb.AppendLine($"- 循环间隔: {interval.Value.TotalMinutes} 分钟");
        if (cronExpression is not null)
            sb.AppendLine($"- Cron 表达式: {cronExpression} ({CronHelper.Describe(cronExpression)})");

        return ToolResult.Ok(sb.ToString(), new Dictionary<string, object>
        {
            ["action"] = "create",
            ["task_id"] = id,
            ["task_name"] = name!,
            ["status"] = ScheduledTaskStatus.Queued.ToString(),
            ["task_type"] = normalizedTaskType.ToString(),
            ["priority"] = priority,
            ["scheduled_at"] = scheduledAt.ToString("O"),
            ["next_run_at"] = scheduledAt.ToString("O"),
            ["interval_minutes"] = interval?.TotalMinutes ?? 0,
            ["cron_expression"] = cronExpression ?? string.Empty,
            ["source"] = "ai",
        });
    }

    private async Task<ToolResult> ListTasksAsync(JsonElement parameters, CancellationToken ct)
    {
        ScheduledTaskStatus? statusFilter = null;
        if (TryGetString(parameters, ["status", "state"], out var statusText))
        {
            var s = statusText?.ToLowerInvariant();
            statusFilter = s switch
            {
                "queued" or "pending" => ScheduledTaskStatus.Pending,
                "dispatching" => ScheduledTaskStatus.Dispatching,
                "waiting_for_llm" or "waiting_llm" => ScheduledTaskStatus.WaitingForLlm,
                "retry_waiting" or "waiting_retry" => ScheduledTaskStatus.RetryWaiting,
                "running" => ScheduledTaskStatus.Running,
                "blocked" => ScheduledTaskStatus.Blocked,
                "completed" => ScheduledTaskStatus.Completed,
                "failed" => ScheduledTaskStatus.Failed,
                "cancelled" => ScheduledTaskStatus.Cancelled,
                "paused" => ScheduledTaskStatus.Paused,
                _ => null,
            };

            if (statusFilter is null)
                return ToolResult.Error($"未知状态过滤值: {statusText}。可选: queued, dispatching, waiting_for_llm, retry_waiting, running, blocked, completed, failed, cancelled, paused",
                    errorCode: "invalid_status_filter",
                    failureCategory: ToolFailureCategory.ValidationError,
                    suggestedFix: "将 status/state 设置为受支持的状态值。", retryable: false);
        }

        TryGetString(parameters, ["source", "origin"], out string? sourceFilter);

        var tasks = await taskManager.ListTasksAsync(statusFilter, sourceFilter, ct);

        if (tasks.Count == 0)
            return ToolResult.Ok("当前没有计划任务。", BuildTaskListMetadata(tasks, sourceFilter, statusFilter));

        var sb = new StringBuilder();
        sb.AppendLine($"# 计划任务列表 ({tasks.Count} 条)");
        sb.AppendLine();

        foreach (var t in tasks)
        {
            var icon = t.Status switch
            {
                ScheduledTaskStatus.Pending => "⏳",
                ScheduledTaskStatus.Dispatching => "🚀",
                ScheduledTaskStatus.WaitingForLlm => "🧠",
                ScheduledTaskStatus.RetryWaiting => "🔁",
                ScheduledTaskStatus.Running => "▶️",
                ScheduledTaskStatus.Blocked => "🧱",
                ScheduledTaskStatus.Completed => "✅",
                ScheduledTaskStatus.Failed => "❌",
                ScheduledTaskStatus.Cancelled => "🚫",
                ScheduledTaskStatus.Paused => "⏸️",
                _ => "❓",
            };

            var typeLabel = t.Type switch
            {
                ScheduledTaskType.Recurring => "🔄循环",
                ScheduledTaskType.Cron => "⏰Cron",
                _ => "1️⃣一次性",
            };
            sb.AppendLine($"{icon} **{t.Id}** [{typeLabel}] P{t.Priority} — {t.Name}");
            sb.AppendLine($"   状态: {t.Status} | 来源: {t.Source} | 已执行: {t.RunCount}次");
            if (t.CronExpression is not null)
                sb.AppendLine($"   Cron: {t.CronExpression} ({CronHelper.Describe(t.CronExpression)})");
            if (t.NextRunAt.HasValue)
                sb.AppendLine($"   下次执行: {t.NextRunAt.Value:yyyy-MM-dd HH:mm}");
            if (t.LastResult is not null)
                sb.AppendLine($"   上次结果: {Truncate(t.LastResult, 80)}");
            sb.AppendLine();
        }

        return ToolResult.Ok(sb.ToString(), BuildTaskListMetadata(tasks, sourceFilter, statusFilter));
    }

    private async Task<ToolResult> CancelTaskAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!TryGetString(parameters, ["task_id", "id"], out var id))
            return ToolResult.Error("缺少参数: task_id",
                errorCode: "missing_task_id",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "提供 task_id。", retryable: false);

        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("task_id 不能为空",
                errorCode: "empty_task_id",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "确保 task_id 为非空字符串。", retryable: false);

        var task = await taskManager.GetTaskAsync(id, ct);
        if (task is null)
            return ToolResult.Error($"未找到任务: {id}",
                errorCode: "task_not_found",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "先使用 list 或 history 确认任务 ID。", retryable: false);

        if (task.Status is ScheduledTaskStatus.Completed or ScheduledTaskStatus.Failed or ScheduledTaskStatus.Cancelled)
            return ToolResult.Error($"任务当前状态为 {task.Status}，无需再次取消。",
                errorCode: "invalid_cancel_transition",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "仅取消未完成的活跃任务。", retryable: false);

        await taskManager.CancelTaskAsync(id, ct);
        logger.LogInformation("[ScheduledTask] Cancelled: {Id}", id);
        return ToolResult.Ok($"✅ 任务 {id} ({task.Name}) 已取消。", new Dictionary<string, object>
        {
            ["action"] = "cancel",
            ["task_id"] = id,
            ["task_name"] = task.Name,
            ["previous_status"] = task.Status.ToString(),
            ["status"] = ScheduledTaskStatus.Cancelled.ToString(),
        });
    }

    private async Task<ToolResult> PauseTaskAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!TryGetString(parameters, ["task_id", "id"], out var id))
            return ToolResult.Error("缺少参数: task_id",
                errorCode: "missing_task_id",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "提供 task_id。", retryable: false);

        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("task_id 不能为空",
                errorCode: "empty_task_id",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "确保 task_id 为非空字符串。", retryable: false);

        var task = await taskManager.GetTaskAsync(id, ct);
        if (task is null)
            return ToolResult.Error($"未找到任务: {id}",
                errorCode: "task_not_found",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "先使用 list 或 history 确认任务 ID。", retryable: false);

        if (task.Status is not (ScheduledTaskStatus.Pending
            or ScheduledTaskStatus.Dispatching
            or ScheduledTaskStatus.WaitingForLlm
            or ScheduledTaskStatus.RetryWaiting
            or ScheduledTaskStatus.Running))
            return ToolResult.Error($"任务当前状态为 {task.Status}，不支持暂停。",
                errorCode: "invalid_pause_transition",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "仅对已入队、派发中、等待中或运行中的任务执行 pause。", retryable: false);

        await taskManager.PauseTaskAsync(id, ct);
        logger.LogInformation("[ScheduledTask] Paused: {Id}", id);
        return ToolResult.Ok($"⏸️ 任务 {id} 已暂停。使用 resume 恢复。", new Dictionary<string, object>
        {
            ["action"] = "pause",
            ["task_id"] = id,
            ["task_name"] = task.Name,
            ["previous_status"] = task.Status.ToString(),
            ["status"] = ScheduledTaskStatus.Paused.ToString(),
        });
    }

    private async Task<ToolResult> ResumeTaskAsync(JsonElement parameters, CancellationToken ct)
    {
        if (!TryGetString(parameters, ["task_id", "id"], out var id))
            return ToolResult.Error("缺少参数: task_id",
                errorCode: "missing_task_id",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "提供 task_id。", retryable: false);

        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Error("task_id 不能为空",
                errorCode: "empty_task_id",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "确保 task_id 为非空字符串。", retryable: false);

        var task = await taskManager.GetTaskAsync(id, ct);
        if (task is null)
            return ToolResult.Error($"未找到任务: {id}",
                errorCode: "task_not_found",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "先使用 list 或 history 确认任务 ID。", retryable: false);

        if (task.Status is not ScheduledTaskStatus.Paused)
            return ToolResult.Error($"任务当前状态为 {task.Status}，只有已暂停任务可以恢复。",
                errorCode: "invalid_resume_transition",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "先将任务置于 Paused 状态，再执行 resume。", retryable: false);

        await taskManager.ResumeTaskAsync(id, ct);
        logger.LogInformation("[ScheduledTask] Resumed: {Id}", id);
        return ToolResult.Ok($"▶️ 任务 {id} 已恢复。", new Dictionary<string, object>
        {
            ["action"] = "resume",
            ["task_id"] = id,
            ["task_name"] = task.Name,
            ["previous_status"] = task.Status.ToString(),
            ["status"] = ScheduledTaskStatus.Pending.ToString(),
        });
    }

    private async Task<ToolResult> GetHistoryAsync(JsonElement parameters, CancellationToken ct)
    {
        TryGetString(parameters, ["task_id", "id"], out var taskId);
        var limit = TryGetInt32(parameters, ["limit", "count", "top"], out var lim) ? lim : 20;

        if (limit <= 0)
            return ToolResult.Error("history 的 limit 必须大于 0。",
                errorCode: "invalid_history_limit",
                failureCategory: ToolFailureCategory.ValidationError,
                suggestedFix: "将 limit、count 或 top 设置为正整数。", retryable: false);

        limit = Math.Min(limit, 200);

        var records = await taskManager.GetExecutionHistoryAsync(taskId, limit, ct);

        if (records.Count == 0)
            return ToolResult.Ok("暂无执行历史记录。", BuildHistoryMetadata(records, taskId, limit));

        var sb = new StringBuilder();
        sb.AppendLine($"# 任务执行历史 ({records.Count} 条)");
        sb.AppendLine();

        foreach (var r in records)
        {
            var icon = r.Success ? "✅" : "❌";
            sb.AppendLine($"{icon} [{r.ExecutedAt:MM-dd HH:mm}] {r.TaskName} — 耗时 {r.Duration.TotalSeconds:F1}s, 工具调用 {r.ToolCallCount} 次");
            if (r.Summary is not null)
                sb.AppendLine($"   {Truncate(r.Summary, 80)}");
            if (!string.IsNullOrWhiteSpace(r.PrimaryFailureSummary))
                sb.AppendLine($"   主失败原因: {Truncate(r.PrimaryFailureSummary, 80)}");
            if (!string.IsNullOrWhiteSpace(r.ToolOverview))
                sb.AppendLine($"   工具摘要: {Truncate(r.ToolOverview, 120)}");
        }

        return ToolResult.Ok(sb.ToString(), BuildHistoryMetadata(records, taskId, limit));
    }

    private async Task<ToolResult> GetStatusAsync(CancellationToken ct)
    {
        var stats = await taskManager.GetStatsAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("# 调度器状态");
        sb.AppendLine();
        sb.AppendLine($"- 总任务数: {stats.TotalTasks}");
        sb.AppendLine($"- 已入队: {stats.Pending}");
        sb.AppendLine($"- 派发中: {stats.Dispatching}");
        sb.AppendLine($"- 等待 LLM: {stats.WaitingForLlm}");
        sb.AppendLine($"- 等待重试: {stats.RetryWaiting}");
        sb.AppendLine($"- 正在执行: {stats.Running}");
        sb.AppendLine($"- 已阻塞: {stats.Blocked}");
        sb.AppendLine($"- 已完成: {stats.Completed}");
        sb.AppendLine($"- 失败: {stats.Failed}");
        sb.AppendLine($"- 已暂停: {stats.Paused}");
        sb.AppendLine($"- 活跃循环任务: {stats.RecurringActive}");

        if (stats.NextScheduledRun.HasValue)
            sb.AppendLine($"- 下次调度: {stats.NextScheduledRun.Value:yyyy-MM-dd HH:mm}");

        return ToolResult.Ok(sb.ToString(), new Dictionary<string, object>
        {
            ["action"] = "status",
            ["total_tasks"] = stats.TotalTasks,
            ["queued"] = stats.Pending,
            ["dispatching"] = stats.Dispatching,
            ["waiting_for_llm"] = stats.WaitingForLlm,
            ["waiting_retry"] = stats.RetryWaiting,
            ["running"] = stats.Running,
            ["blocked"] = stats.Blocked,
            ["completed"] = stats.Completed,
            ["failed"] = stats.Failed,
            ["paused"] = stats.Paused,
            ["recurring_active"] = stats.RecurringActive,
            ["next_scheduled_run"] = stats.NextScheduledRun?.ToString("O") ?? string.Empty,
        });
    }

    private static Dictionary<string, object> BuildTaskListMetadata(
        IReadOnlyList<ScheduledTask> tasks,
        string? sourceFilter,
        ScheduledTaskStatus? statusFilter)
    {
        var items = tasks.Select(t => new Dictionary<string, object>
        {
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["status"] = t.Status.ToString(),
            ["type"] = t.Type.ToString(),
            ["priority"] = t.Priority,
            ["source"] = t.Source,
            ["run_count"] = t.RunCount,
            ["next_run_at"] = t.NextRunAt?.ToString("O") ?? string.Empty,
            ["last_result"] = t.LastResult ?? string.Empty,
            ["cron_expression"] = t.CronExpression ?? string.Empty,
        }).Cast<object>().ToList();

        return new Dictionary<string, object>
        {
            ["action"] = "list",
            ["count"] = tasks.Count,
            ["source_filter"] = sourceFilter ?? string.Empty,
            ["status_filter"] = statusFilter?.ToString() ?? string.Empty,
            ["items"] = items,
        };
    }

    private static Dictionary<string, object> BuildHistoryMetadata(
        IReadOnlyList<TaskExecutionRecord> records,
        string? taskId,
        int limit)
    {
        var items = records.Select(r => new Dictionary<string, object>
        {
            ["id"] = r.Id,
            ["task_id"] = r.TaskId,
            ["task_name"] = r.TaskName,
            ["success"] = r.Success,
            ["summary"] = r.Summary ?? string.Empty,
            ["primary_failure_summary"] = r.PrimaryFailureSummary ?? string.Empty,
            ["tool_overview"] = r.ToolOverview ?? string.Empty,
            ["tool_call_count"] = r.ToolCallCount,
            ["duration_ms"] = (long)r.Duration.TotalMilliseconds,
            ["executed_at"] = r.ExecutedAt.ToString("O"),
        }).Cast<object>().ToList();

        return new Dictionary<string, object>
        {
            ["action"] = "history",
            ["task_id"] = taskId ?? string.Empty,
            ["limit"] = limit,
            ["count"] = records.Count,
            ["items"] = items,
        };
    }

    private static bool TryGetString(JsonElement parameters, IEnumerable<string> names, out string? value)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(parameters, name, out var element))
                continue;

            if (element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }

            if (element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                value = element.ToString();
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetInt32(JsonElement parameters, IEnumerable<string> names, out int value)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(parameters, name, out var element))
                continue;

            if (element.TryGetInt32(out value))
                return true;

            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString()?.Trim(), out value))
                return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement parameters, string name, out JsonElement value)
    {
        if (parameters.TryGetProperty(name, out value))
            return true;

        foreach (var property in parameters.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "...");
}
