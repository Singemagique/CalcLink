using System.Reflection;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalcLink.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly WindowService _window;
    private readonly PickerService _pickers;
    private readonly DialogService _dialogs;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, WindowService window, PickerService pickers,
        DialogService dialogs, ConnectionService connection)
    {
        _settings = settings;
        _window = window;
        _pickers = pickers;
        _dialogs = dialogs;
        Connection = connection;

        _loading = true;
        var s = settings.Current;
        ThemeIndex = (int)s.Theme;
        BackendIndex = (int)s.Backend;
        DefaultReceiveFolder = s.DefaultReceiveFolder ?? string.Empty;
        LinkTimeoutSeconds = s.LinkTimeoutSeconds;
        _loading = false;
    }

    public ConnectionService Connection { get; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartRequired))]
    public partial int BackendIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultFolder))]
    public partial string DefaultReceiveFolder { get; set; }

    [ObservableProperty]
    public partial double LinkTimeoutSeconds { get; set; }

    public bool HasDefaultFolder => !string.IsNullOrEmpty(DefaultReceiveFolder);

    public bool RestartRequired => (CalculatorBackend)BackendIndex != _settings.StartupBackend;

    public string CurrentBackendText => Connection.Service is null
        ? $"Current backend: unavailable — {Connection.ServiceError}"
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
}
