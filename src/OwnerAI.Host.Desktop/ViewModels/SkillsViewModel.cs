using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Host.Desktop.Services;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.ViewModels;

/// <summary>
/// 技能树 ViewModel — 从 DI 工具 + OpenClaw 外部技能 + 进化缺口 组合为技能树展示
/// </summary>
public sealed partial class SkillsViewModel : ObservableObject, IDisposable
{
    private readonly ISkillStateManager _skillState;
    private readonly IEnumerable<IOwnerAITool> _tools;
    private readonly OpenClawSkillScanner _openClawScanner;
    private readonly SkillPluginManager _pluginManager;
    private readonly IEvolutionManager? _evolutionManager;
    private readonly DispatcherQueue _dispatcher;
    private IDisposable? _evolutionSubscription;

    /// <summary>
    /// 平铺列表：SkillCategoryHeader、SkillItem、EvolutionGapItem 交替出现
    /// </summary>
    public ObservableCollection<object> FlatItems { get; } = [];

    [ObservableProperty]
    public partial int TotalSkillCount { get; set; }

    [ObservableProperty]
    public partial int BuiltInCount { get; set; }

    [ObservableProperty]
    public partial int ExternalCount { get; set; }

    [ObservableProperty]
    public partial int EnabledCount { get; set; }

    /// <summary>重复技能数量（用于导航栏提醒角标）</summary>
    [ObservableProperty]
    public partial int DuplicateCount { get; set; }

    /// <summary>进化缺口数量</summary>
    [ObservableProperty]
    public partial int EvolutionGapCount { get; set; }

    /// <summary>正在进化的数量</summary>
    [ObservableProperty]
    public partial int EvolvingCount { get; set; }

    /// <summary>已解决的进化数量</summary>
    [ObservableProperty]
    public partial int ResolvedCount { get; set; }

    private int _plannedGapCount;
    public int PlannedGapCount
    {
        get => _plannedGapCount;
        set => SetProperty(ref _plannedGapCount, value);
    }

    private int _planningCount;
    public int PlanningCount
    {
        get => _planningCount;
        set => SetProperty(ref _planningCount, value);
    }

    private int _implementingCount;
    public int ImplementingCount
    {
        get => _implementingCount;
        set => SetProperty(ref _implementingCount, value);
    }

    private int _verifyingCount;
    public int VerifyingCount
    {
        get => _verifyingCount;
        set => SetProperty(ref _verifyingCount, value);
    }

    public SkillsViewModel(
        IEnumerable<IOwnerAITool> tools,
        OpenClawSkillScanner openClawScanner,
        ISkillStateManager skillState,
        SkillPluginManager pluginManager,
        IEvolutionManager? evolutionManager = null)
    {
        _skillState = skillState;
        _tools = tools;
        _openClawScanner = openClawScanner;
        _pluginManager = pluginManager;
        _evolutionManager = evolutionManager;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        SubscribeEvolutionEvents();
        _ = RebuildListAsync();
    }

    /// <summary>订阅进化事件 — 缺口增删改时自动刷新技能树</summary>
    private void SubscribeEvolutionEvents()
    {
        try
        {
            var eventBus = App.Services.GetService<IEventBus>();
            if (eventBus is null) return;

            _evolutionSubscription = eventBus.Subscribe<EvolutionStatusEvent>(
                (_, _) =>
                {
                    _dispatcher.TryEnqueue(() => _ = RebuildListAsync());
                    return ValueTask.CompletedTask;
                });
        }
        catch
        {
            // Non-critical — evolution events are optional
        }
    }

    public void Dispose()
    {
        _evolutionSubscription?.Dispose();
    }

    /// <summary>
    /// 手动扫描指定文件夹并刷新列表
    /// </summary>
    public void ScanFolder(string folderPath)
    {
        _openClawScanner.AddSearchPath(folderPath);
        _ = RebuildListAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RebuildListAsync();
    }

    private async Task RebuildListAsync()
    {
        FlatItems.Clear();

        // ── 内置工具 ──
        var items = new List<SkillItem>();
        foreach (var tool in _tools)
        {
            var attr = tool.GetType().GetCustomAttribute<ToolAttribute>();
            if (attr is null) continue;

            items.Add(new SkillItem
            {
                Name = attr.Name,
                DisplayName = GetDisplayName(attr.Name),
                Description = attr.Description,
                Glyph = GetGlyph(attr.Name),
                Category = GetCategory(attr.Name),
                SecurityLabel = GetSecurityLabel(attr.SecurityLevel),
                SecurityColor = GetSecurityColor(attr.SecurityLevel),
                IsEnabled = _skillState.IsEnabled(attr.Name),
            });
        }

        BuiltInCount = items.Count;

        // ── OpenClaw 外部技能 ──
        var externalSkills = _openClawScanner.Scan();
        ExternalCount = externalSkills.Count;
        foreach (var skill in externalSkills)
            skill.IsEnabled = _skillState.IsEnabled(skill.Name);
        items.AddRange(externalSkills);

        TotalSkillCount = items.Count;
        EnabledCount = items.Count(i => i.IsEnabled);

        // ── 重复检测 ──
        var nameGroups = items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();
        foreach (var item in nameGroups)
            item.IsDuplicate = true;
        DuplicateCount = nameGroups.Count;

        // ── 加载进化缺口 ──
        var gapItems = new List<EvolutionGapItem>();
        if (_evolutionManager is not null)
        {
            try
            {
                var gaps = await _evolutionManager.ListGapsAsync(ct: default);
                foreach (var gap in gaps)
                {
                    var planSteps = await _evolutionManager.GetPlanStepsAsync(gap.Id, default);
                    var totalSteps = planSteps.Count;
                    var completedSteps = planSteps.Count(s => s.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped);
                    var failedSteps = planSteps.Count(s => s.Status == PlanStepStatus.Failed);
                    var nextStep = await _evolutionManager.GetNextPendingStepAsync(gap.Id, default);

                    gapItems.Add(new EvolutionGapItem
                    {
                        Id = gap.Id,
                        Description = gap.Description,
                        State = MapGapState(gap.Status),
                        StateLabel = GetGapStateLabel(gap.Status),
                        StateColor = GetGapStateColor(gap.Status),
                        Glyph = GetGapGlyph(gap.Status),
                        Priority = gap.Priority,
                        AttemptCount = gap.AttemptCount,
                        Resolution = gap.Resolution,
                        Category = "待进化能力",
                        HasPlan = totalSteps > 0,
                        TotalSteps = totalSteps,
                        CompletedSteps = completedSteps,
                        FailedSteps = failedSteps,
                        PlanProgressText = totalSteps == 0
                            ? "未生成实现计划"
                            : $"计划 {completedSteps}/{totalSteps}" + (failedSteps > 0 ? $" · 失败 {failedSteps}" : string.Empty),
                        StatusDetailText = BuildGapStatusDetail(gap.Status, totalSteps, completedSteps, failedSteps),
                        NextStepTitle = nextStep?.Title,
                        NextStepText = nextStep is null ? "" : $"下一步：{nextStep.Title}",
                    });
                }

                EvolutionGapCount = gapItems.Count(g => g.State is SkillNodeState.Pending);
                EvolvingCount = gapItems.Count(g => g.State is SkillNodeState.Evolving);
                ResolvedCount = gapItems.Count(g => g.State is SkillNodeState.Resolved);
                PlannedGapCount = gapItems.Count(g => g.HasPlan);
                PlanningCount = gaps.Count(g => g.Status == EvolutionGapStatus.Planning);
                ImplementingCount = gaps.Count(g => g.Status == EvolutionGapStatus.Implementing);
                VerifyingCount = gaps.Count(g => g.Status == EvolutionGapStatus.Verifying);
            }
            catch
            {
                // Evolution manager may not be available
            }
        }

        // ── 按类别分组输出 ──
        var categoryOrder = new List<string>
        {
            "高级能力", "文件系统", "系统操作", "网络搜索", "媒体下载", "办公文档", "OpenClaw 技能"
        };
        var groups = items
            .GroupBy(i => i.Category)
            .OrderBy(g => categoryOrder.IndexOf(g.Key) is var idx && idx >= 0 ? idx : 98);

        foreach (var group in groups)
        {
            FlatItems.Add(new SkillCategoryHeader { Category = group.Key, Count = group.Count() });
            foreach (var skill in group)
                FlatItems.Add(skill);
        }

        // ── 进化能力分组 ──
        if (gapItems.Count > 0)
        {
            FlatItems.Add(new SkillCategoryHeader
            {
                Category = "待进化能力",
                Count = gapItems.Count(g => g.State is SkillNodeState.Pending or SkillNodeState.Evolving),
                GapCount = gapItems.Count,
            });
            // 按优先级排序，进化中优先
            foreach (var gap in gapItems.OrderByDescending(g => g.State == SkillNodeState.Evolving)
                                        .ThenByDescending(g => g.Priority))
            {
                FlatItems.Add(gap);
            }
        }
    }

    private static SkillNodeState MapGapState(EvolutionGapStatus status) => status switch
    {
        EvolutionGapStatus.Detected or EvolutionGapStatus.Deferred => SkillNodeState.Pending,
        EvolutionGapStatus.Planning or EvolutionGapStatus.Implementing or EvolutionGapStatus.Verifying => SkillNodeState.Evolving,
        EvolutionGapStatus.Resolved => SkillNodeState.Resolved,
        EvolutionGapStatus.Failed => SkillNodeState.Failed,
        _ => SkillNodeState.Pending,
    };

    private static string GetGapStateLabel(EvolutionGapStatus status) => status switch
    {
        EvolutionGapStatus.Detected => "已发现",
        EvolutionGapStatus.Planning => "规划中",
        EvolutionGapStatus.Implementing => "实现中",
        EvolutionGapStatus.Verifying => "验证中",
        EvolutionGapStatus.Resolved => "已解决",
        EvolutionGapStatus.Failed => "失败",
        EvolutionGapStatus.Deferred => "延期",
        _ => "未知",
    };

    private static string GetGapStateColor(EvolutionGapStatus status) => status switch
    {
        EvolutionGapStatus.Detected => "#9CA3AF",    // gray
        EvolutionGapStatus.Planning => "#F59E0B",    // amber
        EvolutionGapStatus.Implementing => "#3B82F6", // blue
        EvolutionGapStatus.Verifying => "#8B5CF6",   // purple
        EvolutionGapStatus.Resolved => "#22C55E",    // green
        EvolutionGapStatus.Failed => "#EF4444",      // red
        EvolutionGapStatus.Deferred => "#6B7280",    // gray-500
        _ => "#9CA3AF",
    };

    private static string BuildGapStatusDetail(EvolutionGapStatus status, int totalSteps, int completedSteps, int failedSteps)
    {
        if (totalSteps == 0)
            return status == EvolutionGapStatus.Detected ? "待规划：尚未生成实现计划" : "暂无实现计划";

        return status switch
        {
            EvolutionGapStatus.Planning => $"正在分解实现计划 · 已规划 {totalSteps} 步",
            EvolutionGapStatus.Implementing => $"正在按计划实施 · 已完成 {completedSteps}/{totalSteps}",
            EvolutionGapStatus.Verifying => $"进入验收阶段 · 已完成 {completedSteps}/{totalSteps}",
            EvolutionGapStatus.Resolved => $"已完成交付 · 共 {totalSteps} 步",
            EvolutionGapStatus.Failed => failedSteps > 0 ? $"计划执行失败 · 失败 {failedSteps} 步" : "实施失败，待重试",
            _ => $"待处理 · 已完成 {completedSteps}/{totalSteps}",
        };
    }

    private static string GetGapGlyph(EvolutionGapStatus status) => status switch
    {
        EvolutionGapStatus.Detected => "\uE7BA",     // Warning
        EvolutionGapStatus.Planning => "\uE823",     // Clock
        EvolutionGapStatus.Implementing => "\uE945", // Rocket
        EvolutionGapStatus.Verifying => "\uE9D5",    // Shield
        EvolutionGapStatus.Resolved => "\uE73E",     // Checkmark
        EvolutionGapStatus.Failed => "\uEA39",       // Error
        EvolutionGapStatus.Deferred => "\uE769",     // Pause
        _ => "\uE7BA",
    };

    private static string GetDisplayName(string toolName) => toolName switch
    {
        "read_file" => "读取文件",
        "write_file" => "写入文件",
        "list_directory" => "浏览目录",
        "search_files" => "搜索文件",
        "system_info" => "系统信息",
        "run_command" => "执行命令",
        "process_list" => "进程管理",
        "open_app" => "打开应用",
        "clipboard" => "剪贴板",
        "web_fetch" => "获取网页",
        "web_search" => "搜索引擎",
        "download_file" => "下载文件",
        "download_video" => "下载视频",
        "document_tool" => "文档操作",
        "delegate_to_model" => "多模型协作",
        "schedule_task" => "任务调度",
        "self_evolve" => "自我进化",
        "openclaw_skill" => "外部技能",
        _ => toolName,
    };

    private static string GetGlyph(string toolName) => toolName switch
    {
        "read_file" => "\uE736",        // ReadingMode
        "write_file" => "\uE70F",       // Edit
        "list_directory" => "\uE8B7",   // OpenFolderHorizontal
        "search_files" => "\uE721",     // Search
        "system_info" => "\uE7F4",      // Diagnostic
        "run_command" => "\uE756",      // CommandPrompt
        "process_list" => "\uE9D9",     // Processing
        "open_app" => "\uE8A7",         // OpenWith
        "clipboard" => "\uE77F",        // Paste
        "web_fetch" => "\uE774",        // Globe
        "web_search" => "\uE721",       // Search
        "download_file" => "\uE896",    // Download
        "download_video" => "\uE714",   // Video
        "document_tool" => "\uE8A5",    // Document
        "delegate_to_model" => "\uE902",// People
        "schedule_task" => "\uE823",    // Clock
        "self_evolve" => "\uE945",      // Rocket / DNA
        "openclaw_skill" => "\uE946",    // Puzzle
        _ => "\uE946",                  // Puzzle
    };

    private static string GetCategory(string toolName) => toolName switch
    {
        "read_file" or "write_file" or "list_directory" or "search_files" => "文件系统",
        "system_info" or "run_command" or "process_list" or "open_app" or "clipboard" => "系统操作",
        "web_fetch" or "web_search" => "网络搜索",
        "download_file" or "download_video" => "媒体下载",
        "document_tool" => "办公文档",
        "delegate_to_model" or "schedule_task" or "self_evolve" or "openclaw_skill" => "高级能力",
        _ => "其他",
    };

    private static string GetSecurityLabel(ToolSecurityLevel level) => level switch
    {
        ToolSecurityLevel.ReadOnly => "安全",
        ToolSecurityLevel.Low => "低风险",
        ToolSecurityLevel.Medium => "中风险",
        ToolSecurityLevel.High => "高风险",
        ToolSecurityLevel.Critical => "危险",
        _ => "未知",
    };

    private static string GetSecurityColor(ToolSecurityLevel level) => level switch
    {
        ToolSecurityLevel.ReadOnly => "#22C55E",  // green
        ToolSecurityLevel.Low => "#6366F1",       // indigo
        ToolSecurityLevel.Medium => "#F59E0B",    // amber
        ToolSecurityLevel.High => "#F97316",      // orange
        ToolSecurityLevel.Critical => "#EF4444",  // red
        _ => "#9CA3AF",
    };

    /// <summary>
    /// 切换技能开关 — 持久化并实时影响 AI 工具链
    /// </summary>
    [RelayCommand]
    private void ToggleSkill(SkillItem skill)
    {
        _skillState.SetEnabled(skill.Name, skill.IsEnabled);
        EnabledCount = FlatItems.OfType<SkillItem>().Count(i => i.IsEnabled);
    }

    /// <summary>
    /// 删除外部技能 — 从 Skills/ 插件目录中移除并就地更新列表
    /// </summary>
    [RelayCommand]
    private void DeleteSkill(SkillItem skill)
    {
        if (!skill.IsExternal) return;

        _pluginManager.Delete(skill.Name);
        _openClawScanner.InvalidateCache();

        // 就地移除，避免全量刷新
        var idx = FlatItems.IndexOf(skill);
        if (idx >= 0)
        {
            FlatItems.RemoveAt(idx);

            // 更新所属分类标题的计数，若该分类为空则一并移除标题
            if (idx > 0 && FlatItems[idx - 1] is SkillCategoryHeader header)
            {
                var remaining = header.Count - 1;
                if (remaining <= 0)
                    FlatItems.RemoveAt(idx - 1);
                else
                    FlatItems[idx - 1] = new SkillCategoryHeader { Category = header.Category, Count = remaining };
            }
        }

        TotalSkillCount--;
        ExternalCount--;
        if (skill.IsEnabled) EnabledCount--;
        if (skill.IsDuplicate)
        {
            // 重新统计重复
            var allItems = FlatItems.OfType<SkillItem>().ToList();
            var dupSet = allItems.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
            foreach (var item in allItems)
                item.IsDuplicate = dupSet.Contains(item);
            DuplicateCount = dupSet.Count;
        }
    }
}
