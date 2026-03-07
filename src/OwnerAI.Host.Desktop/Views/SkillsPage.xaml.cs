using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Host.Desktop.Services;
using OwnerAI.Host.Desktop.ViewModels;

namespace OwnerAI.Host.Desktop.Views;

public sealed partial class SkillsPage : Page
{
    public SkillsViewModel ViewModel { get; }

    public SkillsPage()
    {
        ViewModel = App.Services.GetRequiredService<SkillsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void SkillToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: SkillItem skill })
        {
            ViewModel.ToggleSkillCommand.Execute(skill);
        }
    }

    private async void DeleteSkill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SkillItem skill }) return;

        var dialog = new ContentDialog
        {
            Title = "删除技能",
            Content = "删除技能将删除 Skill 文件，是否删除？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteSkillCommand.Execute(skill);
        }
    }

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = App.MainWindow;
            if (window is null)
            {
                await ShowErrorDialogAsync("主窗口尚未初始化，请稍后再试。");
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var path = Win32FolderPicker.Show(hwnd, "选择技能目录");
            if (path is not null)
            {
                ViewModel.ScanFolder(path);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync($"无法打开文件夹选择器。\n\n错误: {ex.Message}");
        }
    }

    private async Task ShowErrorDialogAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "操作失败",
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
