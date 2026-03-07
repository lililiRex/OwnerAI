namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// 任务缓存命中模式
/// </summary>
public enum TaskCacheHitMode
{
    /// <summary>未命中 — 相似度 &lt; 0.7，正常执行 ReAct 循环</summary>
    Miss = 0,

    /// <summary>参考命中 — 相似度 0.7-0.95，注入思考链作为 few-shot，仍执行工具链</summary>
    ReferenceHit = 1,

    /// <summary>精确命中 — 相似度 ≥ 0.95 且任务幂等，跳过 ReAct 直接返回缓存结果</summary>
    ExactHit = 2,
}

/// <summary>
/// 任务缓存条目 — 表1: 问题 + 最终结果
/// </summary>
public sealed record TaskCacheEntry
{
    public required string Id { get; init; }
    public required string Query { get; init; }
    public required string Result { get; init; }

    /// <summary>是否幂等任务（纯问答无工具调用）— 仅幂等任务允许精确命中</summary>
    public bool IsIdempotent { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>表2: 思考过程</summary>
    public IReadOnlyList<ThinkingStep> ThinkingSteps { get; init; } = [];

    /// <summary>表3: 工作过程（工具调用链）</summary>
    public IReadOnlyList<WorkStep> WorkSteps { get; init; } = [];
}

/// <summary>
/// 思考步骤 — 表2: LLM 每轮 ReAct 循环的推理文本
/// </summary>
public sealed record ThinkingStep
{
    /// <summary>ReAct 推理轮次 (从 0 开始)</summary>
    public int Round { get; init; }

    /// <summary>该轮的推理/思考文本</summary>
    public required string Reasoning { get; init; }
}

/// <summary>
/// 工作步骤 — 表3: 每次工具调用的记录
/// </summary>
public sealed record WorkStep
{
    /// <summary>ReAct 推理轮次</summary>
    public int Round { get; init; }

    /// <summary>工具名称</summary>
    public required string ToolName { get; init; }

    /// <summary>调用参数 (JSON)</summary>
    public string? Parameters { get; init; }

    /// <summary>执行结果摘要</summary>
    public string? Result { get; init; }

    /// <summary>是否执行成功</summary>
    public bool Success { get; init; }
}

/// <summary>
/// 任务缓存搜索结果
/// </summary>
public sealed record TaskCacheSearchResult
{
    /// <summary>匹配到的缓存条目</summary>
    public required TaskCacheEntry Entry { get; init; }

    /// <summary>相似度得分 (0.0 - 1.0)</summary>
    public float Score { get; init; }

    /// <summary>命中模式 — 由得分和幂等性共同决定</summary>
    public TaskCacheHitMode HitMode { get; init; }
}

/// <summary>
/// 任务缓存管理器接口 — 三表向量缓存的抽象
/// <para>表1: 对话过程 (Query + Result) — 向量索引建在 Query 上</para>
/// <para>表2: 思考过程 (ThinkingSteps) — 外键关联，无需向量索引</para>
/// <para>表3: 工作过程 (WorkSteps) — 外键关联，无需向量索引</para>
/// </summary>
public interface ITaskCacheManager
{
    /// <summary>
    /// 搜索相似任务缓存
    /// </summary>
    /// <param name="query">用户问题</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>最佳匹配结果，若无匹配则返回 null</returns>
    Task<TaskCacheSearchResult?> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// 存储已完成的任务到缓存
    /// </summary>
    /// <param name="entry">任务缓存条目（含思考过程和工作过程）</param>
    /// <param name="ct">取消令牌</param>
    Task StoreAsync(TaskCacheEntry entry, CancellationToken ct = default);
}
