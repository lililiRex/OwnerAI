using Microsoft.UI.Xaml;

namespace OwnerAI.Host.Desktop.Services;

/// <summary>
/// 主题服务实现
/// </summary>
public sealed class ThemeService : IThemeService
{
    public ElementTheme CurrentTheme { get; private set; } = ElementTheme.Default;

    public event Action<ElementTheme>? ThemeChanged;

    public void SetTheme(ElementTheme theme)
    {
        CurrentTheme = theme;
        ThemeChanged?.Invoke(theme);
    }
}
