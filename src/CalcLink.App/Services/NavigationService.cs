using CalcLink.App.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace CalcLink.App.Services;

public enum AppPage { Devices, Calculator, Screen, Tools, Settings }

/// <summary>Navigates the shell's content frame by <see cref="AppPage"/> key.</summary>
public sealed class NavigationService
{
    private static readonly Dictionary<AppPage, Type> Pages = new()
    {
        [AppPage.Devices] = typeof(DevicesPage),
        [AppPage.Calculator] = typeof(CalculatorPage),
        [AppPage.Screen] = typeof(ScreenPage),
        [AppPage.Tools] = typeof(ToolsPage),
        [AppPage.Settings] = typeof(SettingsPage),
    };

    private Frame? _frame;

    public AppPage? Current { get; private set; }

    /// <summary>Raised after navigation so the shell can sync its NavigationView selection.</summary>
    public event EventHandler<AppPage>? Navigated;

    public void Initialize(Frame frame) => _frame = frame;

    public bool Navigate(AppPage page)
    {
        if (_frame is null || Current == page)
        {
            return false;
        }
        bool ok = _frame.Navigate(Pages[page], null, new EntranceNavigationTransitionInfo());
        if (ok)
        {
            Current = page;
            Navigated?.Invoke(this, page);
        }
        return ok;
    }
}
