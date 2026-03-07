using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwnerAI.Host.Desktop.ViewModels;
using OwnerAI.Shared.Abstractions;
using Windows.ApplicationModel.DataTransfer;

namespace OwnerAI.Host.Desktop.Views;

public sealed partial class SchedulerPage : Page
{
    public SchedulerViewModel ViewModel { get; }

    public SchedulerPage()
    {
        ViewModel = App.Services.GetRequiredService<SchedulerViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void OnRunNowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledTask task })
            ViewModel.RunTaskNowCommand.Execute(task);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledTask task })
            ViewModel.EditTaskCommand.Execute(task);
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledTask task })
            ViewModel.PauseTaskCommand.Execute(task);
    }

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledTask task })
            ViewModel.ResumeTaskCommand.Execute(task);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledTask task })
            ViewModel.CancelTaskCommand.Execute(task);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledTask task })
            ViewModel.DeleteTaskCommand.Execute(task);
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: TaskExecutionRecord record })
            ViewModel.SelectedHistoryRecord = record;
    }

    private void OnCloseDetailClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedHistoryRecord = null;
    }

    private void OnCopyDetailLogClick(object sender, RoutedEventArgs e)
    {
        var record = ViewModel.SelectedHistoryRecord;
        if (record is null)
            return;

        var text = record.FullLog;
        if (string.IsNullOrWhiteSpace(text))
            text = record.ToolOverview ?? record.PrimaryFailureSummary ?? record.Summary;

        if (string.IsNullOrWhiteSpace(text))
            return;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
