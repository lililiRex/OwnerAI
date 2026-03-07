using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.ViewModels;

/// <summary>
/// 度量面板 ViewModel — 展示进化度量 + 模型调用度量
/// </summary>
public sealed partial class MetricsViewModel : ObservableObject
{
    private readonly IEvolutionManager _evolutionManager;
    private readonly IModelMetricsManager _metricsManager;
    private readonly DispatcherQueue _dispatcher;

    // ── 进化度量 ──

    [ObservableProperty]
    public partial int TotalGaps { get; set; }

    [ObservableProperty]
    public partial int ResolvedGaps { get; set; }

    [ObservableProperty]
    public partial int FailedGaps { get; set; }

    [ObservableProperty]
    public partial int PendingGaps { get; set; }

    [ObservableProperty]
    public partial int InProgressGaps { get; set; }

    [ObservableProperty]
    public partial double TotalTokenCost { get; set; }

    [ObservableProperty]
    public partial double AvgTokenPerResolved { get; set; }

    [ObservableProperty]
    public partial double AcceptancePassRate { get; set; }

    [ObservableProperty]
    public partial string AvgResolutionTimeText { get; set; } = "-";

    [ObservableProperty]
    public partial string LastEvolutionText { get; set; } = "暂无";

    // ── 模型调用度量 ──

    public ObservableCollection<ModelMetricRow> ModelMetrics { get; } = [];

    [ObservableProperty]
    public partial int TotalModelCalls { get; set; }

    [ObservableProperty]
    public partial int TotalModelTokens { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public MetricsViewModel(
        IEvolutionManager evolutionManager,
        IModelMetricsManager metricsManager)
    {
        _evolutionManager = evolutionManager;
        _metricsManager = metricsManager;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = LoadDataAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            // 进化度量
            var stats = await _evolutionManager.GetEnhancedStatsAsync();
            _dispatcher.TryEnqueue(() =>
            {
                TotalGaps = stats.Basic.TotalGaps;
                ResolvedGaps = stats.Basic.Resolved;
                FailedGaps = stats.Basic.Failed;
                PendingGaps = stats.Basic.Pending;
                InProgressGaps = stats.Basic.InProgress;
                TotalTokenCost = stats.TotalTokenCost;
                AvgTokenPerResolved = stats.AvgTokenPerResolved;
                AcceptancePassRate = stats.AcceptancePassRate;
                AvgResolutionTimeText = stats.AvgResolutionTime.TotalMinutes > 0
                    ? $"{stats.AvgResolutionTime.TotalMinutes:F0} 分钟"
                    : "-";
                LastEvolutionText = stats.Basic.LastEvolutionAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "暂无";
            });

            // 模型调用度量
            var summaries = await _metricsManager.GetAllSummariesAsync();
            _dispatcher.TryEnqueue(() =>
            {
                ModelMetrics.Clear();
                var totalCalls = 0;
                var totalTokens = 0;
                foreach (var s in summaries)
                {
                    ModelMetrics.Add(new ModelMetricRow
                    {
                        ProviderName = s.ProviderName,
                        WorkCategory = s.WorkCategory,
                        TotalCalls = s.TotalCalls,
                        SuccessRate = s.SuccessRate,
                        AvgLatencyMs = s.AvgLatencyMs,
                        TotalTokens = s.TotalTokens,
                    });
                    totalCalls += s.TotalCalls;
                    totalTokens += s.TotalTokens;
                }
                TotalModelCalls = totalCalls;
                TotalModelTokens = totalTokens;
            });
        }
        catch
        {
            // 静默处理，不阻塞 UI
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>模型度量行 — 用于 UI 列表展示</summary>
public sealed record ModelMetricRow
{
    public required string ProviderName { get; init; }
    public required string WorkCategory { get; init; }
    public int TotalCalls { get; init; }
    public double SuccessRate { get; init; }
    public double AvgLatencyMs { get; init; }
    public int TotalTokens { get; init; }
    public string SuccessRateText => $"{SuccessRate:P0}";
    public string LatencyText => $"{AvgLatencyMs:F0} ms";
}
