using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OwnerAI.Host.Desktop.Models;

/// <summary>
/// 工作分类槽位配置项 — 将系统中的具体工作绑定到选定模型
/// </summary>
public sealed partial class WorkModelSlotItem : ObservableObject
{
    public required string SlotValue { get; init; }
    public required string SlotName { get; init; }
    public required string Description { get; init; }
    public ObservableCollection<DisplayItem> ProviderOptions { get; } = [];

    private string _assignedProviderName = string.Empty;
    public string AssignedProviderName
    {
        get => _assignedProviderName;
        set => SetProperty(ref _assignedProviderName, value);
    }

    public DisplayItem? SelectedProviderItem
    {
        get => ProviderOptions.FirstOrDefault(x => x.Value == AssignedProviderName)
            ?? ProviderOptions.FirstOrDefault();
        set
        {
            // 忽略 null — ComboBox 在 ItemsSource 变更/模板回收时触发 TwoWay 写回 null
            if (value is null) return;
            var selectedValue = value.Value;
            if (!string.Equals(AssignedProviderName, selectedValue, StringComparison.Ordinal))
                AssignedProviderName = selectedValue;
        }
    }

    public void RefreshSelectionBinding()
        => OnPropertyChanged(nameof(SelectedProviderItem));
}
