using CalcLink.App.Services;
using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace CalcLink.App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesPage()
    {
        ViewModel = App.GetService<DevicesViewModel>();
        InitializeComponent();
    }

    public DevicesViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.OnNavigatedTo();
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DeviceItem item } && ViewModel.ConnectCommand.CanExecute(item))
        {
            ViewModel.ConnectCommand.Execute(item);
        }
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DisconnectCommand.CanExecute(null))
        {
            ViewModel.DisconnectCommand.Execute(null);
        }
    }

    // Double-click connects, or opens the Calculator page for the already connected device.
    private void OnDeviceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedDevice is { } item && ViewModel.ConnectCommand.CanExecute(item))
        {
            ViewModel.ConnectCommand.Execute(item);
        }
    }

    private async void OnDriverHelpClick(object sender, RoutedEventArgs e)
    {
        await App.GetService<DialogService>().ShowAsync(new DriverHelpDialog());
    }
}
