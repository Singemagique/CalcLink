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
    private readonly DriverSetupService _drivers;
    private readonly DialogService _dialogs;
    private readonly NotificationService _notifications;
    private bool _autoScanned;

    public DevicesViewModel(ConnectionService connection, OperationService operations, NavigationService navigation,
        PendingFilesService pending, DriverSetupService drivers, DialogService dialogs, NotificationService notifications)
    {
        Connection = connection;
        Pending = pending;
        _operations = operations;
        _navigation = navigation;
        _drivers = drivers;
        _dialogs = dialogs;
        _notifications = notifications;
        operations.BusyChanged += (_, _) =>
        {
            ScanCommand.NotifyCanExecuteChanged();
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            SetUpDriverCommand.NotifyCanExecuteChanged();
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
                await CheckDriversAsync(); // a wrong driver can also make the scan fail
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
            await CheckDriversAsync();

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

    // ---- USB driver setup ------------------------------------------------------------------

    /// <summary>The connected TI device that has no driver CalcLink can use, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsDriver), nameof(DriverProblemText))]
    [NotifyCanExecuteChangedFor(nameof(SetUpDriverCommand))]
    public partial UsbDriverDevice? DeviceWithoutDriver { get; private set; }

    public bool NeedsDriver => DeviceWithoutDriver is not null;

    public string DriverProblemText => DeviceWithoutDriver is { } d
        ? $"{d.Description} is plugged in, but it uses {d.DriverText}. CalcLink needs Windows' WinUSB driver for it."
        : string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpDriverCommand))]
    public partial bool IsSettingUpDriver { get; private set; }

    /// <summary>
    /// After every scan on real hardware, asks the helper which driver each connected TI device uses, so
    /// the page can offer to set up WinUSB. Runs even when the scan listed devices: libusb still sees a
    /// SilverLink on TI's driver (it shows up as "Unknown calculator") but can't talk to it.
    /// </summary>
    private async Task CheckDriversAsync()
    {
        if (IsSimulator || !_drivers.IsAvailable)
        {
            DeviceWithoutDriver = null;
            return;
        }
        try
        {
            var devices = await _drivers.ListAsync();
            DeviceWithoutDriver = devices.FirstOrDefault(d => !d.HasUsableDriver);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            DeviceWithoutDriver = null; // the checklist still explains the manual way
        }
    }

    private bool CanSetUpDriver() => DeviceWithoutDriver is not null && !IsSettingUpDriver && !_operations.IsBusy;

    [RelayCommand(CanExecute = nameof(CanSetUpDriver))]
    private async Task SetUpDriverAsync()
    {
        if (DeviceWithoutDriver is not { } device)
        {
            return;
        }
        bool go = await _dialogs.ConfirmAsync(
            "Set up the USB driver?",
            $"CalcLink will assign Windows' built-in WinUSB driver to {device.Description}.\n\n" +
            "Windows will ask for administrator permission next, because installing a driver changes Windows for " +
            "every user of this PC.\n\n" +
            "So that Windows accepts the driver, CalcLink creates a certificate on this PC, signs the driver with it " +
            "and adds it to Windows' trusted certificates. Its private key is deleted straight away, so the " +
            "certificate can't be used to sign anything else.\n\n" +
            "While the device uses WinUSB, TI Connect can't use it. You can switch it back in Device Manager.",
            "Set up driver");
        if (!go)
        {
            return;
        }

        IsSettingUpDriver = true;
        try
        {
            var result = await _drivers.InstallAsync(device.HardwareId);
            if (result.Cancelled)
            {
                return;
            }
            if (!result.Succeeded)
            {
                string details = string.IsNullOrWhiteSpace(result.Log)
                    ? string.Empty
                    : $"\n\nDetails (also saved in {DriverSetupService.LogPath}):\n{result.Log}";
                await _dialogs.ShowMessageAsync("The driver wasn't installed", result.Message + details);
                return;
            }
            if (result.RestartNeeded)
            {
                await _dialogs.ShowMessageAsync("Restart Windows to finish", result.Message);
                return;
            }
            _notifications.Post("USB driver set up", $"{device.Description} now uses WinUSB.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success);
            DeviceWithoutDriver = null;
            // Windows needs a moment to start the device on its new driver.
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (ScanCommand.CanExecute(null))
            {
                await ScanCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            IsSettingUpDriver = false;
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
