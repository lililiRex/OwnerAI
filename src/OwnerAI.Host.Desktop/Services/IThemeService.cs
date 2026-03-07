using Microsoft.UI.Xaml;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 主题服务接口
/// </summary>
public interface IThemeService
{
    ElementTheme CurrentTheme { get; }
    void SetTheme(ElementTheme theme);
    event Action<ElementTheme>? ThemeChanged;
}
