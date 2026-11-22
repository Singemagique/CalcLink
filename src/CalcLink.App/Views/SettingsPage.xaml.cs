using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
