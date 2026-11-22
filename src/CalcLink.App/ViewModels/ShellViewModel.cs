using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.ViewModels;

/// <summary>Main window: connection status bar, transfer panel and the global InfoBar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly NavigationService _navigation;

    public ShellViewModel(ConnectionService connection, OperationService operations,
        NotificationService notifications, NavigationService navigation)
    {
        Connection = connection;
        Operations = operations;
        _navigation = navigation;
        notifications.Posted += (_, n) => Show(n.Title, n.Message, n.Severity);
        connection.ConnectionLost += OnConnectionLost;
        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionService.IsConnected))
            {
                DisconnectCommand.NotifyCanExecuteChanged();
            }
        };
        operations.BusyChanged += (_, _) => DisconnectCommand.NotifyCanExecuteChanged();
    }

    public ConnectionService Connection { get; }

    public OperationService Operations { get; }

    [ObservableProperty]
    public partial bool IsInfoOpen { get; set; }

    [ObservableProperty]
    public partial string InfoTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity InfoSeverity { get; set; }

    public void Show(string title, string message, InfoBarSeverity severity)
    {
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        IsInfoOpen = true;
    }

    private bool CanDisconnect() => Connection.IsConnected && !Operations.IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await Connection.DisconnectAsync();
        _navigation.Navigate(AppPage.Devices);
    }

    private void OnConnectionLost(object? sender, CalcLinkException e)
    {
        // Abort whatever was running against the dead link.
        if (Operations.IsBusy)
        {
            Operations.CancelCommand.Execute(null);
        }
        Show("Connection lost",
            $"The link to the calculator was lost: {Format.Error(e)} Check the cable and connect again.",
            InfoBarSeverity.Error);
        _navigation.Navigate(AppPage.Devices);
    }
}
