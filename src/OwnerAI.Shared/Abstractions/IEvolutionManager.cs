namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 自我进化 — 能力缺口状态
/// </summary>
public enum EvolutionGapStatus
{
    /// <summary>已检测到缺口</summary>
    Detected = 0,
    /// <summary>正在规划解决方案</summary>
    Planning = 1,
    /// <summary>正在实现</summary>
    Implementing = 2,
    /// <summary>正在验证</summary>
    Verifying = 3,
    /// <summary>已解决</summary>
    Resolved = 4,
    /// <summary>实现失败</summary>
    Failed = 5,
    /// <summary>延期处理</summary>
    Deferred = 6,
}

/// <summary>
/// 能力缺口记录
/// </summary>
public sealed record EvolutionGap
{
    /// <summary>唯一 ID</summary>
    public required string Id { get; init; }

    /// <summary>缺口描述 — 缺少什么能力</summary>
    public required string Description { get; init; }

    /// <summary>发现来源: user_feedback / tool_failure / self_analysis / background_scan</summary>
    public string Source { get; init; } = "self_analysis";

    /// <summary>进化类别: source (源码进化) / skill (技能进化)</summary>
    public string Category { get; init; } = "source";

    /// <summary>当前状态</summary>
    public EvolutionGapStatus Status { get; init; } = EvolutionGapStatus.Detected;

    /// <summary>优先级 (1-5，5 最高)</summary>
    public int Priority { get; init; } = 3;

    /// <summary>已尝试次数</summary>
    public int AttemptCount { get; init; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>最后更新时间</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>解决时间</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>解决方案描述</summary>
    public string? Resolution { get; init; }

    /// <summary>最后一次进化尝试的日志</summary>
    public string? LastAttemptLog { get; init; }
}

/// <summary>
/// 进化统计
/// </summary>
public sealed record EvolutionStats
{
    public int TotalGaps { get; init; }
    public int Resolved { get; init; }
    public int Failed { get; init; }
    public int Pending { get; init; }
    public int InProgress { get; init; }
    public DateTimeOffset? LastEvolutionAt { get; init; }
}

// ── 树形实现计划（麦肯锡问题树） ──

/// <summary>
/// 计划步骤状态
/// </summary>
public enum PlanStepStatus
{
    /// <summary>待执行</summary>
    Pending = 0,
    /// <summary>执行中</summary>
    InProgress = 1,
    /// <summary>已完成</summary>
    Completed = 2,
    /// <summary>失败</summary>
    Failed = 3,
    /// <summary>跳过</summary>
    Skipped = 4,
}

/// <summary>
/// 步骤类型 — 麦肯锡问题树区分"诊断"与"方案"层
/// </summary>
public enum PlanStepType
{
    /// <summary>诊断/分析 — Why: 为什么做不到？检查前置条件</summary>
    Diagnostic = 0,
    /// <summary>实现/解决 — How: 具体怎么做？编码/配置/安装</summary>
    Implementation = 1,
    /// <summary>验证/测试 — Verify: 做完了吗？编译/测试/确认</summary>
    Verification = 2,
}

/// <summary>
/// 树形实现计划步骤 — 麦肯锡问题树式分解，每个节点包含假设、验收标准和类型
/// </summary>
public sealed record EvolutionPlanStep
{
    /// <summary>步骤 ID</summary>
    public required string Id { get; init; }

    /// <summary>关联的能力缺口 ID</summary>
    public required string GapId { get; init; }

    /// <summary>父步骤 ID — null 表示根步骤</summary>
    public string? ParentStepId { get; init; }

    /// <summary>步骤标题</summary>
    public required string Title { get; init; }

    /// <summary>步骤详细描述 — 具体要做什么</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>假设 — 为什么需要这一步？预期结果是什么？</summary>
    public string Hypothesis { get; init; } = string.Empty;

    /// <summary>验收标准 — 什么条件下算"完成"？可测量、可验证</summary>
    public string AcceptanceCriteria { get; init; } = string.Empty;

    /// <summary>自动化验证脚本/命令 — 可执行的验收断言 (如 "dotnet test", "ffmpeg -version")</summary>
    public string? VerificationScript { get; init; }

    /// <summary>上次验证脚本的退出码 — 0 为通过</summary>
    public int? VerificationExitCode { get; init; }

    /// <summary>上次验证脚本的输出摘要</summary>
    public string? VerificationOutput { get; init; }

    /// <summary>步骤类型: Diagnostic / Implementation / Verification</summary>
    public PlanStepType StepType { get; init; } = PlanStepType.Implementation;

    /// <summary>同级排序序号</summary>
    public int Order { get; init; }

    /// <summary>步骤深度 (0 = 根)</summary>
    public int Depth { get; init; }

    /// <summary>当前状态</summary>
    public PlanStepStatus Status { get; init; } = PlanStepStatus.Pending;

    /// <summary>步骤执行检查点 — 持久化中间结果，支持崩溃恢复</summary>
    public string? Checkpoint { get; init; }

    /// <summary>执行结果摘要</summary>
    public string? Result { get; init; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>更新时间</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 创建计划步骤的输入 — 支持嵌套子步骤（麦肯锡 MECE 分解）
/// </summary>
public sealed record PlanStepInput
{
    /// <summary>步骤标题</summary>
    public required string Title { get; init; }

    /// <summary>步骤描述</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>假设 — 这一步的前提假设或预期</summary>
    public string Hypothesis { get; init; } = string.Empty;

    /// <summary>验收标准 — SMART 可验证的完成条件</summary>
    public string AcceptanceCriteria { get; init; } = string.Empty;

    /// <summary>自动化验证脚本/命令 — 如 "dotnet test", "ffmpeg -version" (留空则不自动验证)</summary>
    public string? VerificationScript { get; init; }

    /// <summary>步骤类型: diagnostic / implementation / verification</summary>
    public string StepType { get; init; } = "implementation";

    /// <summary>子步骤 — MECE 分解（相互独立、完全穷尽）</summary>
    public List<PlanStepInput> Children { get; init; } = [];
}

/// <summary>
/// 自我进化管理器接口 — 跟踪能力缺口、规划和执行自我改进
/// </summary>
public interface IEvolutionManager
{
    /// <summary>报告新的能力缺口</summary>
    Task<string> ReportGapAsync(string description, string source = "self_analysis", int priority = 3, string category = "source", CancellationToken ct = default);

    /// <summary>获取缺口列表</summary>
    Task<IReadOnlyList<EvolutionGap>> ListGapsAsync(EvolutionGapStatus? status = null, CancellationToken ct = default);

    /// <summary>更新缺口状态</summary>
    Task UpdateGapAsync(string id, EvolutionGapStatus status, string? resolution = null, string? attemptLog = null, CancellationToken ct = default);

    /// <summary>获取下一个待处理的缺口 (按优先级)</summary>
    Task<EvolutionGap?> GetNextPendingGapAsync(CancellationToken ct = default);

    /// <summary>获取进化统计</summary>
    Task<EvolutionStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>检查是否存在相似的缺口 (避免重复报告)</summary>
    Task<bool> HasSimilarGapAsync(string description, CancellationToken ct = default);

    /// <summary>获取下一个需要规划的缺口（通常为 Detected/Failed 且尚无实现计划）</summary>
    Task<EvolutionGap?> GetNextGapForPlanningAsync(CancellationToken ct = default);

    /// <summary>获取下一个需要实施的缺口（已有计划且仍有非验收步骤待执行）</summary>
    Task<EvolutionGap?> GetNextGapForImplementationAsync(CancellationToken ct = default);

    /// <summary>获取下一个需要验收的缺口（实施步骤已完成，进入验证/交付阶段）</summary>
    Task<EvolutionGap?> GetNextGapForVerificationAsync(CancellationToken ct = default);

    // ── 树形实现计划 ──

    /// <summary>为缺口创建树形实现计划</summary>
    Task CreatePlanAsync(string gapId, List<PlanStepInput> steps, CancellationToken ct = default);

    /// <summary>获取缺口的完整实现计划 (树形)</summary>
    Task<IReadOnlyList<EvolutionPlanStep>> GetPlanStepsAsync(string gapId, CancellationToken ct = default);

    /// <summary>获取缺口下一个待执行的步骤 (深度优先，先子后兄)</summary>
    Task<EvolutionPlanStep?> GetNextPendingStepAsync(string gapId, CancellationToken ct = default);

    /// <summary>更新步骤状态</summary>
    Task UpdateStepAsync(string stepId, PlanStepStatus status, string? result = null, CancellationToken ct = default);

    /// <summary>更新步骤的验证结果</summary>
    Task UpdateStepVerificationAsync(string stepId, int exitCode, string? output, CancellationToken ct = default);

    /// <summary>保存步骤检查点 — 持久化中间执行状态，支持崩溃恢复</summary>
    Task SaveStepCheckpointAsync(string stepId, string checkpoint, CancellationToken ct = default);

    /// <summary>检查缺口是否通过验收门 — 所有 Verification 步骤必须有 VerificationExitCode=0</summary>
    Task<AcceptanceGateResult> CheckAcceptanceGateAsync(string gapId, CancellationToken ct = default);

    /// <summary>获取指定步骤</summary>
    Task<EvolutionPlanStep?> GetStepAsync(string stepId, CancellationToken ct = default);

    /// <summary>记录进化度量数据</summary>
    Task RecordEvolutionMetricAsync(EvolutionMetric metric, CancellationToken ct = default);

    /// <summary>获取进化增强统计（含度量）</summary>
    Task<EvolutionEnhancedStats> GetEnhancedStatsAsync(CancellationToken ct = default);

    /// <summary>检查缺口是否已有实现计划</summary>
    Task<bool> HasPlanAsync(string gapId, CancellationToken ct = default);
}

// ── 验收门结果 ──

/// <summary>
/// 验收门检查结果
/// </summary>
public sealed record AcceptanceGateResult
{
    /// <summary>是否通过验收门</summary>
    public bool Passed { get; init; }

    /// <summary>通过的验证步骤数</summary>
    public int PassedCount { get; init; }

    /// <summary>失败的验证步骤数</summary>
    public int FailedCount { get; init; }

    /// <summary>未执行验证脚本的验证步骤数</summary>
    public int NotVerifiedCount { get; init; }

    /// <summary>详细信息</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>失败步骤列表</summary>
    public IReadOnlyList<string> FailedSteps { get; init; } = [];
}

// ── 进化度量 ──

/// <summary>
/// 进化度量记录 — 跟踪进化 ROI
/// </summary>
public sealed record EvolutionMetric
{
    /// <summary>关联的缺口 ID</summary>
    public required string GapId { get; init; }

    /// <summary>度量类型: token_cost / task_success / skill_usage</summary>
    public required string MetricType { get; init; }

    /// <summary>度量值</summary>
    public double Value { get; init; }

    /// <summary>附加信息</summary>
    public string? Detail { get; init; }

    /// <summary>记录时间</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 进化增强统计 — 在基础统计上增加度量维度
/// </summary>
public sealed record EvolutionEnhancedStats
{
    /// <summary>基础统计</summary>
    public required EvolutionStats Basic { get; init; }

    /// <summary>总消耗 Token 数</summary>
    public double TotalTokenCost { get; init; }

    /// <summary>已解决缺口的平均 Token 消耗</summary>
    public double AvgTokenPerResolved { get; init; }

    /// <summary>验收门通过率</summary>
    public double AcceptancePassRate { get; init; }

    /// <summary>平均解决时间</summary>
    public TimeSpan AvgResolutionTime { get; init; }
}
