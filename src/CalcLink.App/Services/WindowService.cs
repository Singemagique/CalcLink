using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace CalcLink.App.Services;

/// <summary>Owns the main window: handle (for pickers), XamlRoot (for dialogs), dispatcher and theme.</summary>
public sealed class WindowService
{
    private Window? _window;

    public Window Window => _window ?? throw new InvalidOperationException("Main window not created yet.");

    public nint Hwnd { get; private set; }

    public DispatcherQueue DispatcherQueue { get; private set; } = DispatcherQueue.GetForCurrentThread();

    public FrameworkElement? Root => _window?.Content as FrameworkElement;

    public XamlRoot? XamlRoot => Root?.XamlRoot;

    public ElementTheme ActualTheme => Root?.ActualTheme ?? ElementTheme.Default;

    public void Attach(Window window)
    {
        _window = window;
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        DispatcherQueue = window.DispatcherQueue;
        if (Root is { } root)
        {
            root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        }
    }

    /// <summary>Unpackaged apps must associate WinRT pickers with a window handle.</summary>
    public void InitializeWithWindow(object pickerOrDialog) =>
        WinRT.Interop.InitializeWithWindow.Initialize(pickerOrDialog, Hwnd);

    public void ApplyTheme(AppTheme theme)
    {
        if (Root is { } root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        UpdateCaptionButtons();
    }

    /// <summary>Caption buttons don't follow an in-app theme override, so colour them explicitly.</summary>
    private void UpdateCaptionButtons()
    {
        if (_window is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        try
        {
            var bar = _window.AppWindow.TitleBar;
            bool dark = ActualTheme == ElementTheme.Dark;
            Color fg = dark ? Colors.White : Colors.Black;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = fg;
            bar.ButtonHoverForegroundColor = fg;
            bar.ButtonPressedForegroundColor = fg;
            bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x99, 0x99, 0x99) : Color.FromArgb(0xFF, 0x77, 0x77, 0x77);
            bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0x00, 0x00, 0x00);
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }
}
