namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 导航服务接口
/// </summary>
public interface INavigationService
{
    void NavigateTo(string pageKey);
    void GoBack();
    bool CanGoBack { get; }
    event Action<string>? Navigated;
}
