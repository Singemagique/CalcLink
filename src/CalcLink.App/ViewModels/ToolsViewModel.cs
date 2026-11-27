using System.Collections.ObjectModel;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

/// <summary>Calculator info, clock, backup, ROM dump and OS upgrade.</summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly OperationService _operations;
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private readonly NotificationService _notifications;

    public ToolsViewModel(ConnectionService connection, OperationService operations, DialogService dialogs,
        PickerService pickers, NotificationService notifications, BackupsViewModel backups)
    {
        Connection = connection;
        Backups = backups;
        _operations = operations;
        _dialogs = dialogs;
        _pickers = pickers;
        _notifications = notifications;

        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConnectionService.Info) or nameof(ConnectionService.Session))
            {
                RebuildInfo();
            }
        };
        connection.SessionChanged += (_, _) =>
        {
            ClockText = "Not read yet";
            UpdateState();
        };
        operations.BusyChanged += (_, _) => UpdateState();

        var now = DateTime.Now;
        ClockDate = new DateTimeOffset(now.Date);
        ClockTime = now.TimeOfDay;
        ClockText = "Not read yet";
        RebuildInfo();
    }

    public ConnectionService Connection { get; }

    /// <summary>The Backups card (Back up now, past backups, restore).</summary>
    public BackupsViewModel Backups { get; }

    public ObservableCollection<InfoRow> InfoRows { get; } = [];

    public bool HasInfo => Connection.Info is not null;
    public bool NoInfo => Connection.IsConnected && Connection.Info is null;

    public bool ShowPython => Connection.Info?.PythonOnBoard == true;
    public bool ShowExactMath => Connection.Info?.ExactMath == true;
    public bool ShowColor => Connection.Info?.ColorScreen == true;
    public bool ShowBatteryLow => Connection.Info?.BatteryOk == false;

    [ObservableProperty]
    public partial string ClockText { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset ClockDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan ClockTime { get; set; }

    private bool Idle => Connection.IsConnected && !_operations.IsBusy;

    public bool CanRefreshInfo => Idle;
    public bool CanClock => Idle && HasClock;
    public bool CanDumpRom => Idle && Connection.Supports(CalcFeatures.RomDump);
    public bool CanSendOs => Idle && Connection.Supports(CalcFeatures.Os);
    public bool CanLeaveExamMode => Idle && Connection.Session?.CanLeaveExamMode == true;

    /// <summary>The connected model has a clock we can read and set: the Clock card shows its pickers and buttons.</summary>
    public bool HasClock => Connection.Supports(CalcFeatures.Clock) && Connection.Info?.ClockSupport != false;

    /// <summary>The OS card's "before upgrading" warning only makes sense where Send OS is available.</summary>
    public bool ShowOsWarning => Connection.Supports(CalcFeatures.Os);

    // One-line notes that replace a card's controls (or explain disabled ones) when a feature is unavailable.
    // Tooltips can't do this job: WinUI doesn't show them on disabled controls.

    public string ClockNote =>
        !Connection.IsConnected ? "Connect a calculator to read or set its clock."
        : HasClock ? string.Empty
        : $"The {Connection.ModelName} has no clock that CalcLink can set.";

    public string RomNote =>
        !Connection.IsConnected ? "Connect a calculator to dump its ROM."
        : Connection.Supports(CalcFeatures.RomDump) ? string.Empty
        : $"ROM dump isn't supported on the {Connection.ModelName}.";

    public string OsNote =>
        !Connection.IsConnected ? "Connect a calculator to upgrade its operating system."
        : Connection.Supports(CalcFeatures.Os) ? string.Empty
        : $"OS upgrades aren't supported on the {Connection.ModelName}.";

    public string ExamNote =>
        !Connection.IsConnected ? "Connect a calculator to end its exam mode."
        : Connection.Session?.CanLeaveExamMode == true ? string.Empty
        : $"The {Connection.ModelName} has no exam mode that CalcLink can end.";

    public void OnNavigatedTo()
    {
        RebuildInfo();
        UpdateState();
        Backups.OnNavigatedTo();
    }

    private void UpdateState()
    {
        OnPropertyChanged(string.Empty);
        RefreshInfoCommand.NotifyCanExecuteChanged();
        ReadClockCommand.NotifyCanExecuteChanged();
        SetClockCommand.NotifyCanExecuteChanged();
        SyncClockCommand.NotifyCanExecuteChanged();
        DumpRomCommand.NotifyCanExecuteChanged();
        SendOsCommand.NotifyCanExecuteChanged();
        LeaveExamModeCommand.NotifyCanExecuteChanged();
    }

    private void RebuildInfo()
    {
        InfoRows.Clear();
        if (Connection.Info is { } i)
        {
            Add("Model", string.IsNullOrEmpty(i.ProductName) ? ModelNames.Of(i.Model) : i.ProductName);
            // Same wording as the calculator's About screen (2nd MEM, About).
            Add("ID", CalcIdentity.FormatId(string.IsNullOrEmpty(i.CalcId) ? i.ProductId : i.CalcId));
            Add("Product code", CalcIdentity.ProductCode(i.Model, i.CalcId));
            Add("OS version", i.OsVersion);
            Add("Boot version", i.BootVersion);
            if (i.RamUser > 0 || i.RamFree > 0)
            {
                Add("RAM", Format.Memory(i.RamFree, i.RamUser));
            }
            if (i.FlashUser > 0 || i.FlashFree > 0)
            {
                Add("Archive (Flash)", Format.Memory(i.FlashFree, i.FlashUser));
            }
            if (i.LcdWidth > 0 && i.LcdHeight > 0)
            {
                Add("Screen", $"{i.LcdWidth} × {i.LcdHeight}, {i.BitsPerPixel} bpp{(i.ColorScreen == true ? ", colour" : string.Empty)}");
            }
            Add("Battery", i.BatteryOk switch { true => "OK", false => "Low — charge or replace before upgrading", null => string.Empty });
            Add("Python", YesNo(i.PythonOnBoard));
            Add("Exact math", YesNo(i.ExactMath));
            Add("Clock", YesNo(i.ClockSupport));
            if (i.RunLevel != 0)
            {
                Add("Run level", i.RunLevel switch { 1 => "1 (boot loader)", 2 => "2 (OS)", var r => r.ToString() });
            }
        }
        UpdateState();

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                InfoRows.Add(new InfoRow(label, value));
            }
        }
        static string YesNo(bool? b) => b switch { true => "Yes", false => "No", null => string.Empty };
    }

    [RelayCommand(CanExecute = nameof(CanRefreshInfo))]
    private async Task RefreshInfoAsync() =>
        await _operations.RunAsync("Reading calculator information", (_, ct) => Connection.RefreshInfoAsync(ct));

    // --- Clock -----------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanClock))]
    private async Task ReadClockAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        var (outcome, clock) = await _operations.RunAsync("Reading the clock", (_, ct) => session.GetClockAsync(ct));
        if (outcome == OperationOutcome.Succeeded && clock is not null)
        {
            ClockText = clock.Value.ToString("F");
            ClockDate = new DateTimeOffset(clock.Value.Date);
            ClockTime = clock.Value.TimeOfDay;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClock))]
    private Task SetClockAsync() => WriteClockAsync(ClockDate.Date + ClockTime);

    [RelayCommand(CanExecute = nameof(CanClock))]
    private Task SyncClockAsync()
    {
        var now = DateTime.Now;
        ClockDate = new DateTimeOffset(now.Date);
        ClockTime = now.TimeOfDay;
        return WriteClockAsync(now);
    }

    private async Task WriteClockAsync(DateTime value)
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        // Only the date and time change: 0 tells the backend to keep the calculator's display formats.
        var clock = new CalcClock(value, TimeFormat: 0, DateFormat: 0);
        var outcome = await _operations.RunAsync("Setting the clock", (_, ct) => session.SetClockAsync(clock, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            ClockText = value.ToString("F");
            _notifications.Post("Clock set", $"The calculator clock is now {value:F}.", InfoBarSeverity.Success);
        }
    }

    // --- ROM / OS (backups: BackupsViewModel) ----------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanDumpRom))]
    private async Task DumpRomAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        bool ok = await _dialogs.ConfirmAsync("Dump the calculator ROM?",
            "A ROM image contains Texas Instruments' copyrighted software. Only dump a ROM from a calculator you own, " +
            "for your own use (for example with an emulator), and never share it.\n\n" +
            "The dump can take several minutes. Keep the calculator connected and switched on.",
            "I own this calculator — dump ROM");
        if (!ok)
        {
            return;
        }
        string? path = await PickSaveAsync($"{Connection.ModelName} ROM",
            [new("ROM image (.rom)", [".rom"])]);
        if (path is null)
        {
            return;
        }
        var outcome = await _operations.RunAsync("Dumping the ROM",
            (progress, ct) => session.DumpRomAsync(path, progress, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            _notifications.Post("ROM dump complete", $"Saved to {path}.", InfoBarSeverity.Success);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendOs))]
    private async Task SendOsAsync()
    {
        if (Connection.Session is not { } session || Connection.Service is not { } service)
        {
            return;
        }
        string? path;
        try
        {
            path = await _pickers.PickFileAsync(ModelNames.OsExtensions);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the file picker", ex);
            return;
        }
        if (path is null)
        {
            return;
        }

        TiFileClass kind;
        CalcModel fileModel;
        try
        {
            kind = service.ClassifyFile(path, out fileModel);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not read the OS file", ex);
            return;
        }
        if (kind != TiFileClass.Os)
        {
            await _dialogs.ShowMessageAsync("Not an OS file", $"{Path.GetFileName(path)} is not a calculator operating system.");
            return;
        }

        if (!await ConfirmOsAsync(path, fileModel))
        {
            return;
        }

        var outcome = await _operations.RunAsync($"Sending OS {Path.GetFileName(path)}",
            (progress, ct) => session.SendFilesAsync([path], progress, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            _notifications.Post("OS sent",
                "The operating system was transferred. The calculator will restart; reconnect once it has finished.",
                InfoBarSeverity.Success);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLeaveExamMode))]
    private async Task LeaveExamModeAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        bool nspire = session.Model == CalcModel.Nspire;
        bool ok = await _dialogs.ConfirmAsync("Leave exam mode?",
            nspire
                ? "CalcLink asks the TI-Nspire to end Press-to-Test. The calculator then restarts, which closes the " +
                  "connection. Save any open document on the calculator first."
                : "CalcLink copies the calculator's window settings (the RclWindw variable) to the PC and straight back. " +
                  "That ends Press-to-Test, as linking to a computer does; nothing else on the calculator changes.",
            "Leave exam mode");
        if (!ok)
        {
            return;
        }
        var outcome = await _operations.RunAsync("Leaving exam mode", (_, ct) => session.LeaveExamModeAsync(ct));
        if (outcome != OperationOutcome.Succeeded)
        {
            return;
        }
        if (nspire)
        {
            // It restarts: the link is gone, so don't wait for the next command to find out.
            await Connection.DisconnectAsync();
            _notifications.Post("Exam mode request sent",
                "The TI-Nspire is restarting. Reconnect once it shows its home screen.", InfoBarSeverity.Success);
        }
        else
        {
            _notifications.Post("Exam mode request sent",
                "Check the calculator: if it was in exam mode, its exam light stops blinking.", InfoBarSeverity.Success);
        }
    }

    private async Task<bool> ConfirmOsAsync(string path, CalcModel fileModel)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 500 };
        bool? battery = Connection.Info?.BatteryOk;
        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = battery == false ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
            Title = battery == false ? "The calculator reports a LOW battery" : "Do not unplug the calculator",
            Message = battery == false
                ? "Charge or replace the batteries before upgrading. A power loss during an OS transfer can leave the calculator unusable until it is recovered."
                : "Keep the cable connected and the calculator powered until the transfer completes. Interrupting an OS upgrade can leave the calculator in its boot loader.",
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"File: {Path.GetFileName(path)}\nCalculator: {Connection.ModelName}" +
                   (fileModel != CalcModel.None && fileModel != Connection.Session?.Model
                       ? $"\nThis file is for: {ModelNames.Of(fileModel)}"
                       : string.Empty),
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = battery is null
                ? "CalcLink can't read the battery state of this model. Make sure the batteries are fresh or charged."
                : "Variables in RAM may be cleared by the upgrade. Back up first if unsure.",
            FontWeight = FontWeights.SemiBold,
        });
        return await _dialogs.ConfirmAsync("Upgrade the operating system?", panel,
            battery == false ? "Upgrade anyway" : "Upgrade OS", destructive: true);
    }

    private async Task<string?> PickSaveAsync(string name, IEnumerable<KeyValuePair<string, IList<string>>> choices)
    {
        try
        {
            return await _pickers.PickSaveFileAsync(name, choices);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the save dialog", ex);
            return null;
        }
    }
}
