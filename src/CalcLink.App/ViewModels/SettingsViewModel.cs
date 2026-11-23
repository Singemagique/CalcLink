using System.Diagnostics;
using System.Reflection;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.AppLifecycle;

namespace CalcLink.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly WindowService _window;
    private readonly PickerService _pickers;
    private readonly DialogService _dialogs;
    private readonly OperationService _operations;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, WindowService window, PickerService pickers,
        DialogService dialogs, ConnectionService connection, OperationService operations)
    {
        _settings = settings;
        _window = window;
        _pickers = pickers;
        _dialogs = dialogs;
        _operations = operations;
        Connection = connection;

        _loading = true;
        var s = settings.Current;
        ThemeIndex = (int)s.Theme;
        BackendIndex = (int)s.Backend;
        DefaultReceiveFolder = s.DefaultReceiveFolder ?? string.Empty;
        LinkTimeoutSeconds = s.LinkTimeoutSeconds;
        _loading = false;

        // The default folder can also be set from the Calculator page's Receive menu.
        settings.Changed += (_, _) =>
        {
            string folder = settings.Current.DefaultReceiveFolder ?? string.Empty;
            if (folder != DefaultReceiveFolder)
            {
                _loading = true;
                DefaultReceiveFolder = folder;
                _loading = false;
            }
        };
    }

    public ConnectionService Connection { get; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartRequired))]
    public partial int BackendIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultFolder), nameof(DefaultReceiveFolderText))]
    public partial string DefaultReceiveFolder { get; set; }

    [ObservableProperty]
    public partial double LinkTimeoutSeconds { get; set; }

    public bool HasDefaultFolder => !string.IsNullOrEmpty(DefaultReceiveFolder);

    public bool HasCustomBackupFolder => !string.IsNullOrWhiteSpace(_settings.Current.BackupFolder);

    public string BackupFolderText => HasCustomBackupFolder ? _settings.Current.BackupFolder! : $"{Backups.DefaultRoot} (default)";

    [RelayCommand]
    private async Task BrowseBackupFolderAsync()
    {
        try
        {
            if (await _pickers.PickFolderAsync() is { } folder)
            {
                _settings.Update(s => s.BackupFolder = folder);
                OnBackupFolderChanged();
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the folder picker", ex);
        }
    }

    [RelayCommand]
    private void ResetBackupFolder()
    {
        _settings.Update(s => s.BackupFolder = null);
        OnBackupFolderChanged();
    }

    private void OnBackupFolderChanged()
    {
        OnPropertyChanged(nameof(HasCustomBackupFolder));
        OnPropertyChanged(nameof(BackupFolderText));
    }

    public string DefaultReceiveFolderText => HasDefaultFolder ? DefaultReceiveFolder : "Not set";

    public bool RestartRequired => (CalculatorBackend)BackendIndex != _settings.StartupBackend;

    public string CurrentBackendText => Connection.Service is null
        ? $"Current backend: unavailable — {Connection.ServiceError}"
        : Connection.SimulatorReason is { } reason
            ? $"Current backend: {Connection.BackendName}. {reason}"
            : $"Current backend: {Connection.BackendName}";

    public string VersionText
    {
        get
        {
            var asm = typeof(SettingsViewModel).Assembly;
            string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                             ?? asm.GetName().Version?.ToString() ?? "0.0.0";
            int plus = version.IndexOf('+');
            if (plus > 0 && version.Length > plus + 8)
            {
                version = version[..(plus + 8)]; // trim the commit hash
            }
            return $"Version {version} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
        }
    }

    public string SettingsPath => SettingsService.SettingsPath;

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading || value < 0)
        {
            return;
        }
        _settings.Update(s => s.Theme = (AppTheme)value);
        _window.ApplyTheme((AppTheme)value);
    }

    partial void OnBackendIndexChanged(int value)
    {
        if (!_loading && value >= 0)
        {
            _settings.Update(s => s.Backend = (CalculatorBackend)value);
        }
    }

    partial void OnDefaultReceiveFolderChanged(string value)
    {
        if (!_loading)
        {
            _settings.Update(s => s.DefaultReceiveFolder = string.IsNullOrWhiteSpace(value) ? null : value);
        }
    }

    partial void OnLinkTimeoutSecondsChanged(double value)
    {
        if (_loading || double.IsNaN(value))
        {
            return;
        }
        _settings.Update(s => s.LinkTimeoutSeconds = Math.Clamp(value, 0.5, 60));
    }

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        try
        {
            if (await _pickers.PickFolderAsync() is { } folder)
            {
                DefaultReceiveFolder = folder;
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the folder picker", ex);
        }
    }

    [RelayCommand]
    private void ClearFolder() => DefaultReceiveFolder = string.Empty;

    /// <summary>Relaunches CalcLink so the selected backend takes effect.</summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        if (_operations.IsBusy)
        {
            await _dialogs.ShowMessageAsync("Can't restart yet",
                "An operation is still running. Wait for it to finish or cancel it, then restart.");
            return;
        }

        // Release the calculator cleanly before the process goes away.
        await Connection.DisconnectAsync();

        string reason;
        try
        {
            // Give up "the running CalcLink" first, or the new process could find this one still
            // registered and hand itself over to it as it exits (Program.RedirectToRunningInstance).
            AppInstance.GetCurrent().UnregisterKey();
            // Terminates this process and starts a new instance on success; returns only on failure.
            reason = AppInstance.Restart(string.Empty).ToString();
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }
        await _dialogs.ShowMessageAsync("Couldn't restart CalcLink",
            $"Close CalcLink and start it again to switch to the selected backend. ({reason})");
    }

    [RelayCommand]
    private async Task OpenSettingsFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDataFolder);
            var args = File.Exists(SettingsService.SettingsPath)
                ? $"/select,\"{SettingsService.SettingsPath}\""
                : $"\"{SettingsService.AppDataFolder}\"";
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the settings folder", ex);
        }
    }
}
