namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 计划任务类型
/// </summary>
public enum ScheduledTaskType
{
    /// <summary>一次性任务 — 指定时间执行一次</summary>
    OneTime = 0,
    /// <summary>循环任务 — 按间隔重复执行</summary>
    Recurring = 1,
    /// <summary>Cron 任务 — 按 Cron 表达式调度</summary>
    Cron = 2,
}

/// <summary>
/// 计划任务状态
/// </summary>
public enum ScheduledTaskStatus
{
    /// <summary>已入队，等待调度</summary>
    Queued = 0,
    /// <summary>兼容旧名称：等待执行</summary>
    Pending = Queued,
    /// <summary>正在执行</summary>
    Running = 1,
    /// <summary>已完成 (仅 OneTime)</summary>
    Completed = 2,
    /// <summary>执行失败</summary>
    Failed = 3,
    /// <summary>已取消</summary>
    Cancelled = 4,
    /// <summary>已暂停</summary>
    Paused = 5,
    /// <summary>等待 LLM / 用户空闲</summary>
    WaitingForLlm = 6,
    /// <summary>等待重试 / 下一轮继续</summary>
    RetryWaiting = 7,
    /// <summary>兼容路线图命名：等待重试</summary>
    WaitingRetry = RetryWaiting,
    /// <summary>调度器已选中，准备派发到后台执行</summary>
    Dispatching = 8,
    /// <summary>任务被外部环境/权限/依赖阻塞</summary>
    Blocked = 9,
}

/// <summary>
/// 计划任务记录
/// </summary>
public sealed record ScheduledTask
{
    /// <summary>唯一 ID</summary>
    public required string Id { get; init; }

    /// <summary>任务名称 (人类可读)</summary>
    public required string Name { get; init; }

    /// <summary>任务描述</summary>
    public string? Description { get; init; }

    /// <summary>任务类型</summary>
    public ScheduledTaskType Type { get; init; } = ScheduledTaskType.OneTime;

    /// <summary>当前状态</summary>
    public ScheduledTaskStatus Status { get; init; } = ScheduledTaskStatus.Pending;

    /// <summary>发送给 Agent 的提示词模板</summary>
    public required string MessageTemplate { get; init; }

    /// <summary>Agent 人设 (为空时使用默认配置)</summary>
    public string? Persona { get; init; }

    /// <summary>Temperature (为 null 时使用默认配置)</summary>
    public float? Temperature { get; init; }

    /// <summary>优先级 (1-5，5 最高；高优先级任务优先执行)</summary>
    public int Priority { get; init; } = 3;

    /// <summary>计划执行时间 (OneTime) / 首次执行时间 (Recurring)</summary>
    public DateTimeOffset ScheduledAt { get; init; } = DateTimeOffset.Now;

    /// <summary>循环间隔 (仅 Recurring)</summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>下一次执行时间</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>上次执行时间</summary>
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>已执行次数</summary>
    public int RunCount { get; init; }

    /// <summary>最大重试次数 (单次执行失败后)</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>当前连续失败次数</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>上次执行结果摘要</summary>
    public string? LastResult { get; init; }

    /// <summary>来源标签 — 标识任务创建者 (如 "evolution", "user", "ai")</summary>
    public string Source { get; init; } = "user";

    /// <summary>Cron 表达式 (仅 Cron 类型) — 简化格式: "分 时 日 月 周" (5 段)</summary>
    public string? CronExpression { get; init; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>最后更新时间</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 调度器统计
/// </summary>
public sealed record SchedulerStats
{
    public int TotalTasks { get; init; }
    public int Pending { get; init; }
    public int WaitingForLlm { get; init; }
    public int RetryWaiting { get; init; }
    public int Dispatching { get; init; }
    public int Running { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Blocked { get; init; }
    public int Paused { get; init; }
    public int RecurringActive { get; init; }
    public DateTimeOffset? NextScheduledRun { get; init; }
}

/// <summary>
/// 任务执行历史记录
/// </summary>
public sealed record TaskExecutionRecord
{
    /// <summary>执行记录 ID</summary>
    public required string Id { get; init; }

    /// <summary>关联的任务 ID</summary>
    public required string TaskId { get; init; }

    /// <summary>任务名称 (冗余存储，任务删除后仍可查看)</summary>
    public required string TaskName { get; init; }

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>执行结果摘要</summary>
    public string? Summary { get; init; }

    /// <summary>主失败原因摘要（例如首个关键工具失败）</summary>
    public string? PrimaryFailureSummary { get; init; }

    /// <summary>工具调用聚合摘要</summary>
    public string? ToolOverview { get; init; }

    /// <summary>完整执行日志（含工具调用过程和结果详情）</summary>
    public string? FullLog { get; init; }

    /// <summary>工具调用次数</summary>
    public int ToolCallCount { get; init; }

    /// <summary>执行耗时</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>执行时间</summary>
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 计划任务持久化管理器接口
/// </summary>
public interface IScheduledTaskManager
{
    /// <summary>创建新任务</summary>
    Task<string> CreateTaskAsync(ScheduledTask task, CancellationToken ct = default);

    /// <summary>获取任务</summary>
    Task<ScheduledTask?> GetTaskAsync(string id, CancellationToken ct = default);

    /// <summary>获取下一个待执行任务 (按优先级 + 时间排序)</summary>
    Task<ScheduledTask?> GetNextReadyTaskAsync(CancellationToken ct = default);

    /// <summary>获取下一个待执行任务 — 仅指定来源</summary>
    Task<ScheduledTask?> GetNextReadyTaskBySourceAsync(string source, CancellationToken ct = default);

    /// <summary>获取下一个待执行任务 — 排除指定来源</summary>
    Task<ScheduledTask?> GetNextReadyTaskExcludingSourceAsync(string excludeSource, CancellationToken ct = default);

    /// <summary>列出任务</summary>
    Task<IReadOnlyList<ScheduledTask>> ListTasksAsync(ScheduledTaskStatus? status = null, string? source = null, CancellationToken ct = default);

    /// <summary>更新任务状态和结果</summary>
    Task UpdateTaskAsync(string id, ScheduledTaskStatus status,
        string? lastResult = null,
        DateTimeOffset? nextRunAt = null,
        CancellationToken ct = default);

    /// <summary>尝试将任务原子标记为 Dispatching，避免并发重复派发</summary>
    Task<bool> TryMarkDispatchingAsync(string id, string? lastResult = null, CancellationToken ct = default);

    /// <summary>取消任务</summary>
    Task CancelTaskAsync(string id, CancellationToken ct = default);

    /// <summary>暂停任务</summary>
    Task PauseTaskAsync(string id, CancellationToken ct = default);

    /// <summary>恢复任务</summary>
    Task ResumeTaskAsync(string id, CancellationToken ct = default);

    /// <summary>编辑任务 — 更新名称、描述、类型、消息模板、优先级、间隔、Cron 等字段</summary>
    Task EditTaskAsync(string id, string name, string? description, ScheduledTaskType type,
        string messageTemplate, int priority, TimeSpan? interval, string? cronExpression,
        CancellationToken ct = default);

    /// <summary>删除任务</summary>
    Task DeleteTaskAsync(string id, CancellationToken ct = default);

    /// <summary>获取统计信息</summary>
    Task<SchedulerStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>检查是否存在相同名称的活跃任务</summary>
    Task<bool> HasActiveTaskAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// 确保内置任务存在且配置最新 — 如果同名任务已存在则更新 source/message_template/persona/temperature，否则创建
    /// </summary>
    Task<string> EnsureBuiltInTaskAsync(ScheduledTask task, CancellationToken ct = default);

    /// <summary>记录一次执行历史</summary>
    Task RecordExecutionAsync(TaskExecutionRecord record, CancellationToken ct = default);

    /// <summary>获取任务的执行历史</summary>
    Task<IReadOnlyList<TaskExecutionRecord>> GetExecutionHistoryAsync(string? taskId = null, int limit = 50, CancellationToken ct = default);
}
