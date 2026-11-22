using CalcLink.App.Services;
using CalcLink.App.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CalcLink.App;

public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    private bool _sized;

    public MainWindow()
    {
        ViewModel = App.GetService<ShellViewModel>();
        _navigation = App.GetService<NavigationService>();
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "CalcLink.ico"));
        ApplyBackdrop();

        _navigation.Initialize(ContentFrame);
        _navigation.Navigated += (_, page) => SelectNavItem(page);
        RootGrid.Loaded += OnRootLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
    }

    private bool _closeApproved;
    private bool _closeInProgress;

    // Closing the window used to end the process with the link thread mid-transfer. Now: ask if an
    // operation is running, cancel it and wait for it to stop, close the link, then close for real.
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            return;
        }
        args.Cancel = true; // must be decided synchronously; we close again ourselves below
        if (_closeInProgress)
        {
            return;
        }
        _closeInProgress = true;
        try
        {
            var operations = App.GetService<OperationService>();
            if (operations.IsBusy)
            {
                string what = string.IsNullOrWhiteSpace(operations.StatusText) ? "An operation" : operations.StatusText.TrimEnd('…', '.');
                bool stop = await App.GetService<DialogService>().ConfirmAsync(
                    "Stop the transfer and quit?",
                    $"{what} is still running. Quitting now stops it partway: the calculator keeps only what was " +
                    "fully transferred. If an OS update is interrupted, the calculator will ask for an OS and you'll " +
                    "need to send it again.",
                    "Stop and quit", destructive: true);
                if (!stop)
                {
                    return;
                }
                operations.CancelCommand.Execute(null);
                await WaitUntilIdleAsync(operations, TimeSpan.FromSeconds(15));
            }
            await Task.WhenAny(App.GetService<ConnectionService>().DisconnectAsync(), Task.Delay(TimeSpan.FromSeconds(5)));
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private static Task WaitUntilIdleAsync(OperationService operations, TimeSpan timeout)
    {
        if (!operations.IsBusy)
        {
            return Task.CompletedTask;
        }
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBusyChanged(object? s, EventArgs e)
        {
            if (!operations.IsBusy)
            {
                operations.BusyChanged -= OnBusyChanged;
                idle.TrySetResult();
            }
        }
        operations.BusyChanged += OnBusyChanged;
        return Task.WhenAny(idle.Task, Task.Delay(timeout));
    }

    public ShellViewModel ViewModel { get; }

    // Mica (Windows 11) is opaque: it only tints with the wallpaper colour. There is deliberately no
    // DesktopAcrylic fallback: on Windows 10 that makes the focused window see-through to the desktop.
    // Without Mica, RootGrid keeps its solid theme background from the XAML.
    private void ApplyBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            RootGrid.Background = null;
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (!_sized)
        {
            _sized = true;
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(780 * scale)));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)(720 * scale);
                presenter.PreferredMinimumHeight = (int)(520 * scale);
            }
        }
        _navigation.Navigate(AppPage.Devices);
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse(tag, out AppPage page))
        {
            _navigation.Navigate(page);
        }
    }

    private void SelectNavItem(AppPage page)
    {
        string tag = page.ToString();
        var item = NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            NavView.SelectedItem = item;
        }
    }

    // Hold an auto-closing InfoBar open while the user is pointing at it or has focus in it.
    private bool _infoPointer;
    private bool _infoFocus;

    private void OnInfoBarPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _infoPointer = true;
        ViewModel.SetInfoHeld(true);
    }

    private void OnInfoBarPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _infoPointer = false;
        ViewModel.SetInfoHeld(_infoFocus);
    }

    private void OnInfoBarGotFocus(object sender, RoutedEventArgs e)
    {
        _infoFocus = true;
        ViewModel.SetInfoHeld(true);
    }

    private void OnInfoBarLostFocus(object sender, RoutedEventArgs e)
    {
        _infoFocus = false;
        ViewModel.SetInfoHeld(_infoPointer);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Normally already closed by OnAppWindowClosing; this covers closes that bypass it.
        var connection = App.GetService<ConnectionService>();
        _ = connection.DisconnectAsync();
    }
}
