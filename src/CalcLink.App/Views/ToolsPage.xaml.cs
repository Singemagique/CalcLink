using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CalcLink.App.Views;

public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        ViewModel = App.GetService<ToolsViewModel>();
        InitializeComponent();
    }

    public ToolsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.OnNavigatedTo();
    }
}
