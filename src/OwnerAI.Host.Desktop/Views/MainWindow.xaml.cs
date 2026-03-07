using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwnerAI.Host.Desktop.Services;
using OwnerAI.Host.Desktop.ViewModels;
using Windows.Graphics;
using System.IO;

namespace OwnerAI.Host.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow(MainViewModel viewModel, INavigationService navigation)
    {
        ViewModel = viewModel;
        InitializeComponent();

        // 自定义标题栏
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "OwnerAI — 你的个人 AI 桌面助手";

        // 设置窗口大小 (WinUI 3 需通过 AppWindow 设置)
        AppWindow.Resize(new SizeInt32(960, 680));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        // 设置 Frame 到导航服务
        if (navigation is NavigationService navService)
        {
            navService.SetFrame(ContentFrame);
        }

        // 技能角标
        UpdateSkillsBadge();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SkillsBadgeCount))
                DispatcherQueue.TryEnqueue(UpdateSkillsBadge);
        };

        // 导航到默认页面
        navigation.NavigateTo("Chat");
    }

    private void UpdateSkillsBadge()
    {
        var count = ViewModel.SkillsBadgeCount;
        SkillsBadge.Value = count;
        SkillsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ViewModel.NavigateToCommand.Execute("Settings");
        }
        else if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            ViewModel.NavigateToCommand.Execute(tag);
        }
    }
}
