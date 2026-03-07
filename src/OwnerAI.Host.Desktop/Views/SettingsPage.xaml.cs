using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Host.Desktop.ViewModels;

namespace OwnerAI.Host.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void OnRemoveProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProviderConfigItem provider })
        {
            ViewModel.RemoveProviderCommand.Execute(provider);
        }
    }

    private void OnTestProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProviderConfigItem provider })
        {
            ViewModel.TestProviderCommand.Execute(provider);
        }
    }

    private void OnRemoveAgentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AgentConfigItem agent })
        {
            ViewModel.RemoveAgentCommand.Execute(agent);
        }
    }
}
