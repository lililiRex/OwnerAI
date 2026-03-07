using CommunityToolkit.Mvvm.ComponentModel;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.Models;

/// <summary>
/// 技能节点状态（技能树用）
/// </summary>
public enum SkillNodeState
{
    /// <summary>已激活 — 工具已注册可用</summary>
    Active,
    /// <summary>进化中 — 正在开发/验证的能力</summary>
    Evolving,
    /// <summary>待进化 — 检测到的能力缺口</summary>
    Pending,
    /// <summary>已解决 — 进化完成</summary>
    Resolved,
    /// <summary>失败 — 进化失败</summary>
    Failed,
}

/// <summary>
/// 技能卡片显示模型
/// </summary>
public sealed partial class SkillItem : ObservableObject
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>副标题（外部技能显示英文名）</summary>
    public string? Subtitle { get; init; }
    public required string Glyph { get; init; }
    public required string Category { get; init; }
    public required string SecurityLabel { get; init; }
    public required string SecurityColor { get; init; }

    /// <summary>是否为外部技能（OpenClaw），外部技能支持删除</summary>
    public bool IsExternal { get; init; }

    /// <summary>技能来源目录（外部技能用，删除时使用）</summary>
    public string? SourceDirectory { get; init; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>是否与其他技能重名</summary>
    [ObservableProperty]
    public partial bool IsDuplicate { get; set; }
}

/// <summary>
/// 进化能力缺口显示模型（技能树中的待进化/进化中节点）
/// </summary>
public sealed class EvolutionGapItem
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required SkillNodeState State { get; init; }
    public required string StateLabel { get; init; }
    public required string StateColor { get; init; }
    public required string Glyph { get; init; }
    public required int Priority { get; init; }
    public int AttemptCount { get; init; }
    public string? Resolution { get; init; }
    public string? Category { get; init; }
    public bool HasPlan { get; init; }
    public int TotalSteps { get; init; }
    public int CompletedSteps { get; init; }
    public int FailedSteps { get; init; }
    public string PlanProgressText { get; init; } = "未规划";
    public string StatusDetailText { get; init; } = string.Empty;
    public string? NextStepTitle { get; init; }
    public string NextStepText { get; init; } = string.Empty;
}

/// <summary>
/// 技能分组标题（用于平铺列表中的分隔头）
/// </summary>
public sealed class SkillCategoryHeader
{
    public required string Category { get; init; }
    public required int Count { get; init; }
    public int GapCount { get; init; }
}
