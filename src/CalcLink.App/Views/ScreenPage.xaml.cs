using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CalcLink.App.Views;

public sealed partial class ScreenPage : Page
{
    public ScreenPage()
    {
        ViewModel = App.GetService<ScreenViewModel>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                root.Changed += (_, _) => PushViewport();
            }
            PushViewport();
        };
    }

    public ScreenViewModel ViewModel { get; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.StopLive();
        base.OnNavigatedFrom(e);
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => PushViewport();

    /// <summary>The VM needs the viewport size and DPI scale to render pixel-exact (nearest neighbour).</summary>
    private void PushViewport()
    {
        const double bezel = 2 * 12 + 2; // Border padding + stroke
        ViewModel.UpdateViewport(Viewport.ActualWidth - bezel, Viewport.ActualHeight - bezel,
            XamlRoot?.RasterizationScale ?? 1.0);
    }
}
