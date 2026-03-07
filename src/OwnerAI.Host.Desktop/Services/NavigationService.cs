using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// Frame 导航服务实现 — 启用页面缓存保持对话状态
/// </summary>
public sealed class NavigationService : INavigationService
{
    private Frame? _frame;
    private string _currentPage = string.Empty;

    public bool CanGoBack => _frame?.CanGoBack == true;

    public event Action<string>? Navigated;

    public void SetFrame(Frame frame)
    {
        _frame = frame;
        // 启用页面缓存，导航回聊天页时保留消息
        _frame.CacheSize = 5;
    }

    public void NavigateTo(string pageKey)
    {
        if (_frame is null || pageKey == _currentPage)
            return;

        var pageType = pageKey switch
        {
            "Chat" => typeof(Views.ChatPage),
            "Settings" => typeof(Views.SettingsPage),
            "Scheduler" => typeof(Views.SchedulerPage),
            "Skills" => typeof(Views.SkillsPage),
            "Metrics" => typeof(Views.MetricsPage),
            _ => typeof(Views.ChatPage),
        };

        _currentPage = pageKey;
        _frame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
        Navigated?.Invoke(pageKey);
    }

    public void GoBack()
    {
        if (_frame is { CanGoBack: true })
        {
            _frame.GoBack();
        }
    }
}
