using System.Collections.ObjectModel;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalcLink.App.ViewModels;

public sealed class DeviceItem(DeviceDescriptor device)
{
    public DeviceDescriptor Device { get; } = device;
    public string Name => string.IsNullOrWhiteSpace(Device.DisplayName) ? ModelNames.Of(Device.Model) : Device.DisplayName;
    public string ModelText => ModelNames.Of(Device.Model);
    public string ConnectAutomationName => $"Connect to {Name}";
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
    public string SimulatorMessage =>
        "No native backend or real hardware is in use: the calculators listed here are simulated. " +
        (Connection.SimulatorReason is { } reason ? reason + " " : string.Empty) +
        "Choose the backend in Settings.";
    public string BackendText => $"Backend: {Connection.BackendName}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial DeviceItem? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string EmptyText { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    [ObservableProperty]
    public partial string ScanError { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasScanError { get; set; }

    /// <summary>Called by the page; scans once automatically on first display.</summary>
    public void OnNavigatedTo()
    {
        if (!_autoScanned && HasBackend)
        {
            _autoScanned = true;
            ScanCommand.Execute(null);
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
            SelectedDevice = Devices.FirstOrDefault();
            IsEmpty = Devices.Count == 0;
            EmptyText = IsEmpty
                ? "No calculators found. Connect a calculator with a USB cable, turn it on, then press Scan."
                : string.Empty;

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
        var outcome = await _operations.RunAsync($"Connecting to {target.Name}",
            (_, ct) => Connection.ConnectAsync(target.Device, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            _navigation.Navigate(AppPage.Calculator);
        }
    }
}
