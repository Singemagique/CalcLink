using CalcLink.App.Services;
using CalcLink.App.ViewModels;
using CalcLink.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace CalcLink.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        s_current = this;
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    public static IServiceProvider Services { get; private set; } = null!;

    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<PickerService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<OperationService>();
        services.AddSingleton<PendingFilesService>();
        services.AddSingleton<DriverSetupService>();

        // ViewModels (singletons so page state survives navigation)
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<CalculatorViewModel>();
        services.AddSingleton<ScreenViewModel>();
        services.AddSingleton<FilesViewModel>();
        services.AddSingleton<BackupsViewModel>();
        services.AddSingleton<ToolsViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = GetService<SettingsService>();
        settings.Load();

        // Explorer "Open" / "Send to calculator": one readable file opens on the Files page once the
        // window is up (OnShellReady); anything else is queued and sent after connecting.
        // Later launches are redirected here by Program.Main (OnRedirectedActivation).
        _startup = LaunchArguments.Parse(Environment.GetCommandLineArgs().Skip(1));
        if (_startup.FileToOpen is null && _startup.Files.Count > 0)
        {
            GetService<PendingFilesService>().Add(_startup.Files);
        }

        // Never let a missing/broken backend crash the app: ConnectionService records the error and
        // the Devices page shows it.
        GetService<ConnectionService>().InitializeBackend(settings.StartupBackend);

        _window = new MainWindow();
        GetService<WindowService>().Attach(_window);
        GetService<WindowService>().ApplyTheme(settings.Current.Theme);
        _window.Activate();
    }

    private LaunchArguments _startup = LaunchArguments.None;

    /// <summary>Called by MainWindow once its first page is shown: opens a file CalcLink was started with.</summary>
    internal async void OnShellReady()
    {
        var startup = _startup;
        _startup = LaunchArguments.None;
        if (startup.FileToOpen is { } path)
        {
            await OpenOnFilesPageAsync(path);
        }
    }

    /// <summary>
    /// A second CalcLink was started (Explorer "Open" or "Send to calculator" while CalcLink runs) and
    /// handed us its command line. Raised on a background thread.
    /// </summary>
    internal static void OnRedirectedActivation(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        string commandLine = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? launch.Arguments
            : string.Empty;
        var request = LaunchArguments.Parse(commandLine);
        if (s_current?._window is { } window)
        {
            window.DispatcherQueue.TryEnqueue(() => s_current.HandleRequest(request));
        }
    }

    private static App? s_current;

    private async void HandleRequest(LaunchArguments request)
    {
        BringToFront();
        if (request.FileToOpen is { } path)
        {
            await OpenOnFilesPageAsync(path);
            return;
        }
        if (request.Files.Count == 0)
        {
            return;
        }

        GetService<PendingFilesService>().Add(request.Files);
        var navigation = GetService<NavigationService>();
        if (GetService<ConnectionService>().IsConnected)
        {
            // The Calculator page sends queued files when it's shown; if it's showing already, send now.
            if (!navigation.Navigate(AppPage.Calculator))
            {
                await GetService<CalculatorViewModel>().SendPendingAsync();
            }
            return;
        }
        // Not connected: the Devices page shows the queue, and a scan that finds exactly one
        // calculator connects and sends.
        navigation.Navigate(AppPage.Devices);
        var devices = GetService<DevicesViewModel>();
        if (devices.ScanCommand.CanExecute(null) && !devices.IsScanning)
        {
            devices.ScanCommand.Execute(null);
        }
    }

    private static async Task OpenOnFilesPageAsync(string path)
    {
        var files = GetService<FilesViewModel>();
        await files.LoadAsync(path);
        if (string.Equals(files.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            GetService<NavigationService>().Navigate(AppPage.Files);
        }
    }

    private void BringToFront()
    {
        if (_window is null)
        {
            return;
        }
        if (_window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            {
                State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized,
            } presenter)
        {
            presenter.Restore();
        }
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("Unhandled exception", e.Exception);
        if (_window is null)
        {
            return; // Startup failure: let it crash, the log has the details.
        }

        // Keep the app alive and tell the user instead of disappearing.
        e.Handled = true;
        var ex = e.Exception;
        _ = GetService<DialogService>().ShowErrorAsync("Unexpected error", ex);
    }
}

internal static class CrashLog
{
    public static void Write(string title, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDataFolder);
            File.AppendAllText(Path.Combine(SettingsService.AppDataFolder, "crash.log"),
                $"[{DateTime.Now:O}] {title}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }
}
