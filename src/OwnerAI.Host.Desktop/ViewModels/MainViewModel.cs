using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnerAI.Host.Desktop.Services;

namespace OwnerAI.Host.Desktop.ViewModels;

/// <summary>
/// 主窗口 ViewModel — NavigationView 导航
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly SkillsViewModel _skillsViewModel;

    [ObservableProperty]
    public partial string SelectedNavItem { get; set; } = "Chat";

    /// <summary>技能菜单角标数量（重复技能数）</summary>
    [ObservableProperty]
    public partial int SkillsBadgeCount { get; set; }

    public MainViewModel(INavigationService navigation, SkillsViewModel skillsViewModel)
    {
        _navigation = navigation;
        _skillsViewModel = skillsViewModel;
        _navigation.Navigated += page => SelectedNavItem = page;

        // 监听技能 VM 重复数变化
        SkillsBadgeCount = _skillsViewModel.DuplicateCount;
        _skillsViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SkillsViewModel.DuplicateCount))
                SkillsBadgeCount = _skillsViewModel.DuplicateCount;
        };
    }

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        _navigation.NavigateTo(pageKey);
    }
}
