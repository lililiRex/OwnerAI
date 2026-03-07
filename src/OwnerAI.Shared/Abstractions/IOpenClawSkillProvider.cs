namespace OwnerAI.Shared.Abstractions;

/// <summary>
/// OpenClaw 技能元数据 — 从 SKILL.md 解析
/// </summary>
public sealed record OpenClawSkillInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string SkillDirectory { get; init; }

    /// <summary>中文名称</summary>
    public string? DisplayNameCN { get; init; }

    /// <summary>中文描述</summary>
    public string? DescriptionCN { get; init; }

    /// <summary>SKILL.md 完整内容（用于注入 Agent 上下文）</summary>
    public string? FullContent { get; init; }

    /// <summary>可执行脚本列表（相对路径）</summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];

    /// <summary>技能版本号</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>创建来源: manual / evolution</summary>
    public string Source { get; init; } = "manual";

    /// <summary>技能状态: trial / stable / deprecated</summary>
    public string Status { get; init; } = "stable";

    /// <summary>使用次数</summary>
    public int UsageCount { get; init; }

    /// <summary>成功次数</summary>
    public int SuccessCount { get; init; }

    /// <summary>成功率 (0.0-1.0)</summary>
    public double SuccessRate => UsageCount > 0 ? (double)SuccessCount / UsageCount : 0;

    /// <summary>导入时间</summary>
    public DateTimeOffset? ImportedAt { get; init; }
}

/// <summary>
/// OpenClaw 技能提供者 — 扫描、解析、提供技能元数据
/// </summary>
public interface IOpenClawSkillProvider
{
    /// <summary>获取所有已发现的 OpenClaw 技能</summary>
    IReadOnlyList<OpenClawSkillInfo> GetSkills();

    /// <summary>按名称查找技能</summary>
    OpenClawSkillInfo? FindSkill(string name);
}

/// <summary>
/// 模型调用度量记录 — 用于自适应模型路由
/// </summary>
public sealed record ModelCallMetric
{
    /// <summary>模型供应商名称</summary>
    public required string ProviderName { get; init; }

    /// <summary>工作分类</summary>
    public required string WorkCategory { get; init; }

    /// <summary>响应延迟 (毫秒)</summary>
    public double LatencyMs { get; init; }

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>Token 消耗 (估算)</summary>
    public int TokenCount { get; init; }

    /// <summary>记录时间</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 模型路由度量管理器接口 — 记录和查询模型调用数据
/// </summary>
public interface IModelMetricsManager
{
    /// <summary>记录一次模型调用</summary>
    Task RecordCallAsync(ModelCallMetric metric, CancellationToken ct = default);

    /// <summary>获取指定模型在指定工作分类下的统计</summary>
    Task<ModelPerformanceSummary> GetPerformanceSummaryAsync(string providerName, string workCategory, CancellationToken ct = default);

    /// <summary>获取指定工作分类下所有模型的排名</summary>
    Task<IReadOnlyList<ModelPerformanceSummary>> GetRankingAsync(string workCategory, CancellationToken ct = default);

    /// <summary>获取所有模型在所有分类下的汇总统计</summary>
    Task<IReadOnlyList<ModelPerformanceSummary>> GetAllSummariesAsync(CancellationToken ct = default);
}

/// <summary>
/// 模型性能统计摘要
/// </summary>
public sealed record ModelPerformanceSummary
{
    public required string ProviderName { get; init; }
    public required string WorkCategory { get; init; }
    public int TotalCalls { get; init; }
    public int SuccessCount { get; init; }
    public double AvgLatencyMs { get; init; }
    public double SuccessRate => TotalCalls > 0 ? (double)SuccessCount / TotalCalls : 0;
    public int TotalTokens { get; init; }
}
