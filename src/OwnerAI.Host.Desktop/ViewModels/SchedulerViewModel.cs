using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using OwnerAI.Agent.Scheduler;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.ViewModels;

/// <summary>
/// 任务管理页面 ViewModel — 任务列表、创建、管理、执行历史
/// </summary>
public sealed partial class SchedulerViewModel : ObservableObject, IDisposable
{
    private static readonly string[] s_evolutionPipelineTaskNames = ["进化检查", "进化执行", "进化验收"];

    private readonly IScheduledTaskManager _taskManager;
    private readonly SchedulerService _scheduler;
    private readonly IEvolutionManager? _evolutionManager;
    private readonly DispatcherQueue _dispatcher;
    private IDisposable? _schedulerSubscription;

    public ObservableCollection<ScheduledTask> Tasks { get; } = [];
    public ObservableCollection<ScheduledTask> EvolutionPipelineTasks { get; } = [];
    public ObservableCollection<ScheduledTask> RegularTasks { get; } = [];
    public ObservableCollection<TaskExecutionRecord> HistoryRecords { get; } = [];
    public ObservableCollection<EvolutionPlannedGapViewItem> PlannedEvolutionGaps { get; } = [];

    public bool HasEvolutionPipelineTasks => EvolutionPipelineTasks.Count > 0;
    public bool HasRegularTasks => RegularTasks.Count > 0;
    public bool HasPlannedEvolutionGaps => PlannedEvolutionGaps.Count > 0;

    // ── 调度器状态 ──

    [ObservableProperty]
    public partial bool IsSchedulerActive { get; set; }

    [ObservableProperty]
    public partial string SchedulerPhase { get; set; } = "空闲";

    [ObservableProperty]
    public partial string StatsText { get; set; } = string.Empty;

    private string _evolutionOverviewText = "暂无进化数据";
    public string EvolutionOverviewText
    {
        get => _evolutionOverviewText;
        set => SetProperty(ref _evolutionOverviewText, value);
    }

    private string _nextPlanningGapText = "暂无待规划缺口";
    public string NextPlanningGapText
    {
        get => _nextPlanningGapText;
        set => SetProperty(ref _nextPlanningGapText, value);
    }

    private string _nextImplementationGapText = "暂无待实施缺口";
    public string NextImplementationGapText
    {
        get => _nextImplementationGapText;
        set => SetProperty(ref _nextImplementationGapText, value);
    }

    private string _nextVerificationGapText = "暂无待验收缺口";
    public string NextVerificationGapText
    {
        get => _nextVerificationGapText;
        set => SetProperty(ref _nextVerificationGapText, value);
    }

    // ── 创建任务表单 ──

    [ObservableProperty]
    public partial string NewTaskName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewTaskMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIntervalInput))]
    [NotifyPropertyChangedFor(nameof(ShowCronInput))]
    public partial int SelectedTypeIndex { get; set; }

    [ObservableProperty]
    public partial int NewTaskIntervalMinutes { get; set; } = 60;

    [ObservableProperty]
    public partial string NewTaskCron { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int NewTaskPriority { get; set; } = 3;

    [ObservableProperty]
    public partial string NewTaskDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CronPreview { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowFormMessage { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    // ── 编辑模式 ──

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(FormButtonText));
                OnPropertyChanged(nameof(ShowCancelEdit));
            }
        }
    }
    /// <summary>正在编辑的任务 ID</summary>
    private string? _editingTaskId;

    public string FormButtonText => IsEditing ? "保存修改" : "创建任务";

    public Microsoft.UI.Xaml.Visibility ShowCancelEdit =>
        IsEditing ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ShowIntervalInput =>
        SelectedTypeIndex == 1 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ShowCronInput =>
        SelectedTypeIndex == 2 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    // ── 选项卡 ──

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    /// <summary>当前选中的历史记录（用于展示执行详情）</summary>
    [ObservableProperty]
    public partial TaskExecutionRecord? SelectedHistoryRecord { get; set; }

    public bool HasSelectedRecord => SelectedHistoryRecord is not null;

    public SchedulerViewModel(IScheduledTaskManager taskManager, SchedulerService scheduler, IEvolutionManager? evolutionManager = null)
    {
        _taskManager = taskManager;
        _scheduler = scheduler;
        _evolutionManager = evolutionManager;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        SubscribeSchedulerEvents();
        _ = LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            var tasks = await _taskManager.ListTasksAsync(ct: default);
            Tasks.Clear();
            foreach (var t in tasks)
                Tasks.Add(t);

            EvolutionPipelineTasks.Clear();
            foreach (var t in tasks.Where(IsEvolutionPipelineTask).OrderBy(GetEvolutionPipelineOrder))
                EvolutionPipelineTasks.Add(t);

            RegularTasks.Clear();
            foreach (var t in tasks.Where(t => !IsEvolutionPipelineTask(t)))
                RegularTasks.Add(t);

            OnPropertyChanged(nameof(HasEvolutionPipelineTasks));
            OnPropertyChanged(nameof(HasRegularTasks));
            OnPropertyChanged(nameof(HasPlannedEvolutionGaps));

            var history = await _taskManager.GetExecutionHistoryAsync(limit: 50, ct: default);
            HistoryRecords.Clear();
            foreach (var r in history)
                HistoryRecords.Add(r);

            var stats = await _taskManager.GetStatsAsync();
            StatsText = $"总计 {stats.TotalTasks} | 已入队 {stats.Pending} | 派发中 {stats.Dispatching} | 等待 LLM {stats.WaitingForLlm} | 等待重试 {stats.RetryWaiting} | 运行中 {stats.Running} | 已阻塞 {stats.Blocked} | 已完成 {stats.Completed} | 失败 {stats.Failed} | 暂停 {stats.Paused}";

            await LoadEvolutionVisualizationAsync();
        }
        catch
        {
            StatsText = "加载数据失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task CreateTaskAsync()
    {
        if (IsEditing)
        {
            await SaveEditAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(NewTaskName) || string.IsNullOrWhiteSpace(NewTaskMessage))
        {
            ShowFormMsg("❌ 任务名称和执行消息不能为空");
            return;
        }

        var taskType = SelectedTypeIndex switch
        {
            1 => ScheduledTaskType.Recurring,
            2 => ScheduledTaskType.Cron,
            _ => ScheduledTaskType.OneTime,
        };

        TimeSpan? interval = null;
        string? cronExpression = null;
        DateTimeOffset scheduledAt = DateTimeOffset.Now;

        if (taskType == ScheduledTaskType.Recurring)
        {
            if (NewTaskIntervalMinutes < 1)
            {
                ShowFormMsg("❌ 循环间隔必须为正整数（分钟）");
                return;
            }
            interval = TimeSpan.FromMinutes(NewTaskIntervalMinutes);
        }
        else if (taskType == ScheduledTaskType.Cron)
        {
            if (string.IsNullOrWhiteSpace(NewTaskCron) || !CronHelper.IsValid(NewTaskCron))
            {
                ShowFormMsg("❌ 无效的 Cron 表达式。格式: \"分 时 日 月 周\"，如 \"0 9 * * *\"");
                return;
            }
            cronExpression = NewTaskCron.Trim();
            var nextRun = CronHelper.GetNextOccurrence(cronExpression, DateTimeOffset.Now);
            if (nextRun.HasValue) scheduledAt = nextRun.Value;
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var task = new ScheduledTask
        {
            Id = id,
            Name = NewTaskName.Trim(),
            Description = string.IsNullOrWhiteSpace(NewTaskDescription) ? null : NewTaskDescription.Trim(),
            Type = taskType,
            MessageTemplate = NewTaskMessage.Trim(),
            Priority = Math.Clamp(NewTaskPriority, 1, 5),
            ScheduledAt = scheduledAt,
            Interval = interval,
            NextRunAt = scheduledAt,
            CronExpression = cronExpression,
            Source = "user",
        };

        await _taskManager.CreateTaskAsync(task);
        ShowFormMsg($"✅ 任务 \"{task.Name}\" 已创建 (ID: {id})");
        ResetForm();
        await LoadDataAsync();
    }

    [RelayCommand]
    private void EditTask(ScheduledTask? task)
    {
        if (task is null) return;

        _editingTaskId = task.Id;
        IsEditing = true;
        NewTaskName = task.Name;
        NewTaskMessage = task.MessageTemplate;
        NewTaskDescription = task.Description ?? string.Empty;
        NewTaskPriority = task.Priority;

        SelectedTypeIndex = task.Type switch
        {
            ScheduledTaskType.Recurring => 1,
            ScheduledTaskType.Cron => 2,
            _ => 0,
        };

        NewTaskIntervalMinutes = task.Interval.HasValue
            ? (int)task.Interval.Value.TotalMinutes
            : 60;
        NewTaskCron = task.CronExpression ?? string.Empty;

        SelectedTabIndex = 1; // 切换到创建/编辑表单
    }

    [RelayCommand]
    private async Task RunTaskNowAsync(ScheduledTask? task)
    {
        if (task is null) return;
        await _scheduler.RunTaskNowAsync(task.Id);
        ShowFormMsg($"▶ 任务 \"{task.Name}\" 已加入立即执行队列");
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (_editingTaskId is null) return;

        if (string.IsNullOrWhiteSpace(NewTaskName) || string.IsNullOrWhiteSpace(NewTaskMessage))
        {
            ShowFormMsg("❌ 任务名称和执行消息不能为空");
            return;
        }

        var taskType = SelectedTypeIndex switch
        {
            1 => ScheduledTaskType.Recurring,
            2 => ScheduledTaskType.Cron,
            _ => ScheduledTaskType.OneTime,
        };

        TimeSpan? interval = null;
        string? cronExpression = null;

        if (taskType == ScheduledTaskType.Recurring)
        {
            if (NewTaskIntervalMinutes < 1)
            {
                ShowFormMsg("❌ 循环间隔必须为正整数（分钟）");
                return;
            }
            interval = TimeSpan.FromMinutes(NewTaskIntervalMinutes);
        }
        else if (taskType == ScheduledTaskType.Cron)
        {
            if (string.IsNullOrWhiteSpace(NewTaskCron) || !CronHelper.IsValid(NewTaskCron))
            {
                ShowFormMsg("❌ 无效的 Cron 表达式。格式: \"分 时 日 月 周\"，如 \"0 9 * * *\"");
                return;
            }
            cronExpression = NewTaskCron.Trim();
        }

        await _taskManager.EditTaskAsync(
            _editingTaskId,
            NewTaskName.Trim(),
            string.IsNullOrWhiteSpace(NewTaskDescription) ? null : NewTaskDescription.Trim(),
            taskType,
            NewTaskMessage.Trim(),
            Math.Clamp(NewTaskPriority, 1, 5),
            interval,
            cronExpression);

        ShowFormMsg($"✅ 任务已更新");
        ResetForm();
        await LoadDataAsync();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        ResetForm();
    }

    [RelayCommand]
    private async Task CancelTaskAsync(ScheduledTask? task)
    {
        if (task is null) return;
        await _taskManager.CancelTaskAsync(task.Id);
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task PauseTaskAsync(ScheduledTask? task)
    {
        if (task is null) return;
        await _taskManager.PauseTaskAsync(task.Id);
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task ResumeTaskAsync(ScheduledTask? task)
    {
        if (task is null) return;
        await _taskManager.ResumeTaskAsync(task.Id);
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(ScheduledTask? task)
    {
        if (task is null) return;
        await _taskManager.DeleteTaskAsync(task.Id);
        await LoadDataAsync();
    }

    partial void OnNewTaskCronChanged(string value)
    {
        CronPreview = CronHelper.IsValid(value)
            ? $"✅ {CronHelper.Describe(value)}"
            : string.IsNullOrWhiteSpace(value) ? string.Empty : "❌ 无效格式";
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1)
            _ = LoadDataAsync();
    }

    partial void OnSelectedHistoryRecordChanged(TaskExecutionRecord? value)
    {
        OnPropertyChanged(nameof(HasSelectedRecord));
    }

    private static bool IsEvolutionPipelineTask(ScheduledTask task)
        => task.Source == "evolution" && s_evolutionPipelineTaskNames.Contains(task.Name, StringComparer.Ordinal);

    private static int GetEvolutionPipelineOrder(ScheduledTask task)
        => task.Name switch
        {
            "进化检查" => 0,
            "进化执行" => 1,
            "进化验收" => 2,
            _ => int.MaxValue,
        };

    private async Task LoadEvolutionVisualizationAsync()
    {
        if (_evolutionManager is null)
        {
            EvolutionOverviewText = "未启用进化管理器";
            NextPlanningGapText = "暂无待规划缺口";
            NextImplementationGapText = "暂无待实施缺口";
            NextVerificationGapText = "暂无待验收缺口";
            PlannedEvolutionGaps.Clear();
            OnPropertyChanged(nameof(HasPlannedEvolutionGaps));
            return;
        }

        var gaps = await _evolutionManager.ListGapsAsync(ct: default);
        var plannedCount = 0;
        var plannedGapItems = new List<EvolutionPlannedGapViewItem>();
        foreach (var gap in gaps)
        {
            if (await _evolutionManager.HasPlanAsync(gap.Id, default))
            {
                plannedCount++;
                var steps = await _evolutionManager.GetPlanStepsAsync(gap.Id, default);
                plannedGapItems.Add(BuildPlannedGapItem(gap, steps));
            }
        }

        EvolutionOverviewText = $"待实现技能 {gaps.Count(g => g.Status == EvolutionGapStatus.Detected)} | 已有计划 {plannedCount} | 实施中 {gaps.Count(g => g.Status == EvolutionGapStatus.Implementing)} | 验收中 {gaps.Count(g => g.Status == EvolutionGapStatus.Verifying)}";

        var planningGap = await _evolutionManager.GetNextGapForPlanningAsync(default);
        var implementationGap = await _evolutionManager.GetNextGapForImplementationAsync(default);
        var verificationGap = await _evolutionManager.GetNextGapForVerificationAsync(default);

        NextPlanningGapText = planningGap is null
            ? "暂无待规划缺口"
            : $"规划目标：{planningGap.Description}";
        NextImplementationGapText = implementationGap is null
            ? "暂无待实施缺口"
            : $"实施目标：{implementationGap.Description}";
        NextVerificationGapText = verificationGap is null
            ? "暂无待验收缺口"
            : $"验收目标：{verificationGap.Description}";

        PlannedEvolutionGaps.Clear();
        foreach (var item in plannedGapItems
                     .OrderByDescending(i => i.StatusText)
                     .ThenBy(i => i.Description, StringComparer.OrdinalIgnoreCase))
            PlannedEvolutionGaps.Add(item);

        OnPropertyChanged(nameof(HasPlannedEvolutionGaps));
    }

    private static EvolutionPlannedGapViewItem BuildPlannedGapItem(EvolutionGap gap, IReadOnlyList<EvolutionPlanStep> steps)
    {
        var completed = steps.Count(s => s.Status is PlanStepStatus.Completed or PlanStepStatus.Skipped);
        var running = steps.Count(s => s.Status == PlanStepStatus.InProgress);
        var failed = steps.Count(s => s.Status == PlanStepStatus.Failed);
        var pending = steps.Count(s => s.Status == PlanStepStatus.Pending);

        var stepItems = steps
            .OrderBy(s => s.Depth)
            .ThenBy(s => s.Order)
            .Select(s => new EvolutionPlanStepViewItem
            {
                StepId = s.Id,
                Title = s.Title,
                StepTypeText = s.StepType switch
                {
                    PlanStepType.Diagnostic => "诊断",
                    PlanStepType.Implementation => "实现",
                    PlanStepType.Verification => "验收",
                    _ => s.StepType.ToString(),
                },
                StatusText = s.Status switch
                {
                    PlanStepStatus.Pending => "待执行",
                    PlanStepStatus.InProgress => "执行中",
                    PlanStepStatus.Completed => "已完成",
                    PlanStepStatus.Failed => "失败",
                    PlanStepStatus.Skipped => "已跳过",
                    _ => s.Status.ToString(),
                },
                TitleText = $"{new string('　', s.Depth)}• {s.Title}",
                Result = s.Result,
            })
            .ToList();

        return new EvolutionPlannedGapViewItem
        {
            GapId = gap.Id,
            Description = gap.Description,
            CategoryText = gap.Category == "skill" ? "技能进化" : "源码进化",
            StatusText = gap.Status switch
            {
                EvolutionGapStatus.Detected => "待规划",
                EvolutionGapStatus.Planning => "规划中",
                EvolutionGapStatus.Implementing => "实施中",
                EvolutionGapStatus.Verifying => "验收中",
                EvolutionGapStatus.Resolved => "已解决",
                EvolutionGapStatus.Failed => "失败",
                EvolutionGapStatus.Deferred => "已延期",
                _ => gap.Status.ToString(),
            },
            ProgressText = $"步骤 {completed}/{steps.Count} 完成 | 执行中 {running} | 待执行 {pending} | 失败 {failed}",
            LastAttemptLog = gap.LastAttemptLog,
            Steps = stepItems,
        };
    }

    private void ResetForm()
    {
        _editingTaskId = null;
        IsEditing = false;
        NewTaskName = string.Empty;
        NewTaskMessage = string.Empty;
        NewTaskDescription = string.Empty;
        SelectedTypeIndex = 0;
        NewTaskIntervalMinutes = 60;
        NewTaskCron = string.Empty;
        NewTaskPriority = 3;
    }

    private void ShowFormMsg(string msg)
    {
        FormMessage = msg;
        ShowFormMessage = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            await Task.Delay(3000);
            ShowFormMessage = false;
        });
    }

    private void SubscribeSchedulerEvents()
    {
        try
        {
            var eventBus = App.Services.GetService<IEventBus>();
            if (eventBus is null) return;

            _schedulerSubscription = eventBus.Subscribe<SchedulerStatusEvent>(
                (evt, ct) =>
                {
                    _dispatcher.TryEnqueue(async () =>
                    {
                        IsSchedulerActive = evt.IsActive;
                        SchedulerPhase = evt.Phase;

                        if (evt.Stats is not null)
                        {
                            StatsText = $"总计 {evt.Stats.TotalTasks} | 等待 {evt.Stats.Pending} | 等待 LLM {evt.Stats.WaitingForLlm} | 等待重试 {evt.Stats.RetryWaiting} | 运行中 {evt.Stats.Running} | 已完成 {evt.Stats.Completed} | 失败 {evt.Stats.Failed} | 暂停 {evt.Stats.Paused}";
                        }

                        // 自动刷新任务列表和执行历史（在 UI 线程执行，ObservableCollection 要求）
                        await LoadDataAsync();
                    });
                    return ValueTask.CompletedTask;
                });
        }
        catch
        {
            // Non-critical — scheduler events are optional
        }
    }

    public void Dispose()
    {
        _schedulerSubscription?.Dispose();
    }
}
