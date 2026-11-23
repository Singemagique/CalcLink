using CalcLink.App.Services;
using CalcLink.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace CalcLink.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
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

        // "Open with CalcLink": file paths on the command line are sent after connecting.
        // TODO: single-instance redirection (AppInstance.RedirectActivationToAsync) so opening a file
        // while CalcLink is running reuses the existing window instead of starting a second instance.
        GetService<PendingFilesService>().SetFromCommandLine(Environment.GetCommandLineArgs().Skip(1));

        // Never let a missing/broken backend crash the app: ConnectionService records the error and
        // the Devices page shows it.
        GetService<ConnectionService>().InitializeBackend(settings.StartupBackend);

        _window = new MainWindow();
        GetService<WindowService>().Attach(_window);
        GetService<WindowService>().ApplyTheme(settings.Current.Theme);
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
