using System.Collections.ObjectModel;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalcLink.App.ViewModels;

public sealed partial class DeviceItem(DeviceDescriptor device) : ObservableObject
{
    public DeviceDescriptor Device { get; } = device;
    public string Name => string.IsNullOrWhiteSpace(Device.DisplayName) ? ModelNames.Of(Device.Model) : Device.DisplayName;
    public string ModelText => ModelNames.Of(Device.Model);

    /// <summary>The model line is only shown when it adds something to the name.</summary>
    public bool ShowModelText => !string.Equals(ModelText, Name, StringComparison.OrdinalIgnoreCase);

    public string ConnectAutomationName => $"Connect to {Name}";
    public string DisconnectAutomationName => $"Disconnect from {Name}";

    /// <summary>True while this device is the one with the open session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    public partial bool IsConnected { get; set; }

    public bool IsNotConnected => !IsConnected;

    public string Details
    {
        get
        {
            var parts = new List<string> { ModelNames.Of(Device.Cable) };
            if (Device.VendorId != 0 || Device.ProductId != 0)
            {
                parts.Add($"USB {Device.VendorId:X4}:{Device.ProductId:X4}");
            }
            if (Device.Port > 0)
            {
                parts.Add($"port {Device.Port}");
            }
            return string.Join(" · ", parts);
        }
    }
}

public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly OperationService _operations;
    private readonly NavigationService _navigation;
    private bool _autoScanned;

    public DevicesViewModel(ConnectionService connection, OperationService operations, NavigationService navigation,
        PendingFilesService pending)
    {
        Connection = connection;
        Pending = pending;
        _operations = operations;
        _navigation = navigation;
        operations.BusyChanged += (_, _) =>
        {
            ScanCommand.NotifyCanExecuteChanged();
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
        };
        connection.SessionChanged += (_, _) =>
        {
            UpdateConnectedState();
            DisconnectCommand.NotifyCanExecuteChanged();
        };
        EmptyText = HasBackend
            ? "Press Scan to look for calculators."
            : "Scanning is unavailable because no calculator backend could be started.";
    }

    public ConnectionService Connection { get; }

    public PendingFilesService Pending { get; }

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    public bool HasBackend => Connection.Service is not null;
    public bool HasBackendError => Connection.ServiceError is not null;
    public string BackendError => Connection.ServiceError ?? string.Empty;
    public bool IsSimulator => Connection.IsSimulator;
    // The technical reason (e.g. why the native DLL didn't load) is shown in Settings → Calculator backend.
    public string SimulatorMessage =>
        "The calculators listed here are simulated; no real calculator is used. " +
        (Connection.SimulatorReason is not null
            ? "The real-hardware backend couldn't be loaded; see Settings for details."
            : "Choose the backend in Settings.");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial DeviceItem? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTroubleshooting))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string EmptyText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTroubleshooting))]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>True once a scan has finished (successfully or not).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTroubleshooting))]
    public partial bool HasScanned { get; private set; }

    /// <summary>The "Calculator not found?" checklist: shown after a scan that found nothing.</summary>
    public bool ShowTroubleshooting => HasBackend && HasScanned && IsEmpty && !IsScanning;

    [ObservableProperty]
    public partial string ScanError { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasScanError { get; set; }

    /// <summary>Called by the page; scans once automatically on first display.</summary>
    public void OnNavigatedTo()
    {
        UpdateConnectedState();
        if (!_autoScanned && HasBackend)
        {
            _autoScanned = true;
            ScanCommand.Execute(null);
        }
    }

    private void UpdateConnectedState()
    {
        var connected = Connection.Session?.Device;
        foreach (var item in Devices)
        {
            item.IsConnected = connected is not null && item.Device == connected;
        }
    }

    private bool CanScan() => HasBackend && !_operations.IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        HasScanError = false;
        try
        {
            var (outcome, devices) = await _operations.RunAsync("Scanning for calculators",
                (_, ct) => Connection.ProbeAsync(ct), reportErrors: false);

            if (outcome == OperationOutcome.Failed && _operations.LastError is { } ex)
            {
                ScanError = Format.Error(ex);
                HasScanError = true;
                HasScanned = true;
                EmptyText = IsEmpty ? "The scan didn't complete." : string.Empty;
            }
            if (outcome != OperationOutcome.Succeeded || devices is null)
            {
                return;
            }

            Devices.Clear();
            foreach (var d in devices)
            {
                Devices.Add(new DeviceItem(d));
            }
            UpdateConnectedState();
            SelectedDevice = Devices.FirstOrDefault(i => i.IsConnected) ?? Devices.FirstOrDefault();
            IsEmpty = Devices.Count == 0;
            HasScanned = true;
            EmptyText = IsEmpty ? "No calculators found." : string.Empty;

            // Launched with files ("Open with CalcLink"): connect straight away when the choice is obvious;
            // otherwise the banner asks the user to pick a calculator.
            if (Pending.HasPending && Devices.Count == 1 && !Connection.IsConnected)
            {
                await ConnectAsync(Devices[0]);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanConnect(DeviceItem? item) => (item ?? SelectedDevice) is not null && !_operations.IsBusy;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(DeviceItem? item)
    {
        if ((item ?? SelectedDevice) is not { } target)
        {
            return;
        }
        if (target.IsConnected)
        {
            // Already open: reconnecting would drop and reopen the link for nothing.
            _navigation.Navigate(AppPage.Calculator);
            return;
        }
        var outcome = await _operations.RunAsync($"Connecting to {target.Name}",
            (_, ct) => Connection.ConnectAsync(target.Device, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            _navigation.Navigate(AppPage.Calculator);
        }
    }

    private bool CanDisconnect() => Connection.IsConnected && !_operations.IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => Connection.DisconnectAsync();
}
