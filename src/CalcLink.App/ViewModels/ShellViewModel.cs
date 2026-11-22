using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.ViewModels;

/// <summary>Main window: connection status bar, transfer panel and the global InfoBar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>Success/informational messages close themselves; warnings and errors stay until closed.</summary>
    private static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InformationalDuration = TimeSpan.FromSeconds(7);

    private readonly NavigationService _navigation;
    private readonly WindowService _window;
    private DispatcherQueueTimer? _infoTimer;
    private bool _infoAutoClose;
    private bool _infoHeld;

    public ShellViewModel(ConnectionService connection, OperationService operations,
        NotificationService notifications, NavigationService navigation, WindowService window)
    {
        Connection = connection;
        Operations = operations;
        _navigation = navigation;
        _window = window;
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

        // One timer for the one InfoBar, restarted per message, so an older message's timer can never
        // close a newer message.
        _infoAutoClose = severity is InfoBarSeverity.Success or InfoBarSeverity.Informational;
        StopInfoTimer();
        if (_infoAutoClose)
        {
            StartInfoTimer(severity == InfoBarSeverity.Success ? SuccessDuration : InformationalDuration);
        }
    }

    /// <summary>
    /// The pointer is over the InfoBar or keyboard focus is inside it: don't close it under the user.
    /// The countdown restarts when they leave.
    /// </summary>
    public void SetInfoHeld(bool held)
    {
        if (held == _infoHeld)
        {
            return;
        }
        _infoHeld = held;
        if (held)
        {
            StopInfoTimer();
        }
        else if (IsInfoOpen && _infoAutoClose)
        {
            StartInfoTimer(SuccessDuration);
        }
    }

    partial void OnIsInfoOpenChanged(bool value)
    {
        if (!value)
        {
            StopInfoTimer();
            _infoAutoClose = false;
            _infoHeld = false; // a collapsed bar may never get PointerExited
        }
    }

    private void StartInfoTimer(TimeSpan duration)
    {
        if (_infoHeld)
        {
            return;
        }
        if (_infoTimer is null)
        {
            _infoTimer = _window.DispatcherQueue.CreateTimer();
            _infoTimer.IsRepeating = false;
            _infoTimer.Tick += (_, _) =>
            {
                _infoTimer?.Stop();
                if (_infoAutoClose && !_infoHeld)
                {
                    IsInfoOpen = false;
                }
            };
        }
        _infoTimer.Interval = duration;
        _infoTimer.Start();
    }

    private void StopInfoTimer() => _infoTimer?.Stop();

    private bool CanDisconnect() => Connection.IsConnected && !Operations.IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await Connection.DisconnectAsync();
        _navigation.Navigate(AppPage.Devices);
    }

    private void OnConnectionLost(object? sender, CalcLinkException e)
    {
        // Abort whatever was running against the dead link, without it posting its own "was
        // cancelled" notice or error dialog over this message.
        string stopped = string.Empty;
        if (Operations.IsBusy)
        {
            string title = Operations.Title;
            if (title.Length > 0)
            {
                stopped = $" while {char.ToLower(title[0], System.Globalization.CultureInfo.CurrentCulture)}{title[1..]}";
            }
            Operations.CancelForLinkLoss();
        }
        Show("Connection lost",
            $"The link to the calculator was lost{stopped}: {Format.Error(e)} " +
            "Check the cable and that the calculator is on, then connect again.",
            InfoBarSeverity.Error);
        _navigation.Navigate(AppPage.Devices);
    }
}
