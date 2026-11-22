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
        Closed += OnClosed;
    }

    public ShellViewModel ViewModel { get; }

    private void ApplyBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        else
        {
            RootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Close the link politely; don't block shutdown on it.
        var connection = App.GetService<ConnectionService>();
        _ = connection.DisconnectAsync();
    }
}
