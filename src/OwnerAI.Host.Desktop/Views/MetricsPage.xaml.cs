using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using OwnerAI.Host.Desktop.ViewModels;

namespace OwnerAI.Host.Desktop.Views;

public sealed partial class MetricsPage : Page
{
    public MetricsViewModel ViewModel { get; }

    public MetricsPage()
    {
        ViewModel = App.Services.GetRequiredService<MetricsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }
}
