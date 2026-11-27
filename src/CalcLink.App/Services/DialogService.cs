using CalcLink.App.Helpers;
using CalcLink.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.Services;

/// <summary>ContentDialog helpers. Only one ContentDialog may be open at a time, so calls are serialized.</summary>
public sealed class DialogService(WindowService window)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task ShowMessageAsync(string title, string message) =>
        ShowAsync(new ContentDialog
        {
            Title = title,
            Content = Wrap(message),
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });

    public Task ShowErrorAsync(string title, Exception ex)
    {
        // Message, then what to try (when we know), then the code once, small, for support.
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Wrap(Format.ErrorMessage(ex)));
        if (ErrorHints.For(ex) is { } hint)
        {
            var hintBlock = Wrap(hint);
            hintBlock.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            panel.Children.Add(hintBlock);
        }
        if (Format.ErrorDetails(ex) is { } details)
        {
            var detailsBlock = Wrap(details);
            // The style's ThemeResource foreground follows the dialog's theme (a brush looked up here
            // would follow the app's, which differs when the user overrides the theme in Settings).
            detailsBlock.Style = (Style)Application.Current.Resources["SecondaryCaptionTextStyle"];
            panel.Children.Add(detailsBlock);
        }
        return ShowAsync(new ContentDialog
        {
            Title = title,
            Content = panel,
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });
    }

    public Task<bool> ConfirmAsync(string title, string message, string primaryText, bool destructive = false) =>
        ConfirmAsync(title, Wrap(message), primaryText, destructive);

    public async Task<bool> ConfirmAsync(string title, UIElement content, string primaryText, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            // Destructive actions default to Cancel so Enter doesn't trigger them by accident.
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Asks for a single line of text; returns null if cancelled.</summary>
    public async Task<string?> PromptAsync(string title, string label, string initial = "", string primaryText = "OK")
    {
        var box = new TextBox { Header = label, Text = initial, MinWidth = 320 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initial);
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>Rename dialog with optional folder field; returns null if cancelled.</summary>
    public async Task<(string Folder, string Name)?> PromptRenameAsync(string folder, string name, bool showFolder)
    {
        var nameBox = new TextBox { Header = "New name", Text = name, MinWidth = 320 };
        var folderBox = new TextBox { Header = "Folder", Text = folder, MinWidth = 320 };
        var panel = new StackPanel { Spacing = 12 };
        if (showFolder)
        {
            panel.Children.Add(folderBox);
        }
        panel.Children.Add(nameBox);
        nameBox.SelectAll();

        var dialog = new ContentDialog
        {
            Title = "Rename",
            Content = panel,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }
        return (showFolder ? folderBox.Text.Trim() : folder, nameBox.Text.Trim());
    }

    /// <summary>
    /// "Set up a connection": a cable (SilverLink or GrayLink port) and a calculator model.
    /// Returns the chosen indexes, or null if cancelled. <paramref name="modelIndex"/> -1 = nothing preselected.
    /// </summary>
    public async Task<(int Cable, int Model)?> PickConnectionAsync(string? note, IReadOnlyList<string> cables,
        int cableIndex, IReadOnlyList<string> models, int modelIndex)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 480 };
        if (!string.IsNullOrEmpty(note))
        {
            panel.Children.Add(Wrap(note));
        }
        var cableBox = new ComboBox
        {
            Header = "Cable",
            ItemsSource = cables,
            SelectedIndex = cableIndex,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var modelBox = new ComboBox
        {
            Header = "Calculator",
            ItemsSource = models,
            SelectedIndex = modelIndex,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        panel.Children.Add(cableBox);
        panel.Children.Add(modelBox);
        var help = Wrap("A gray TI-Graph Link plugs into a serial port, on most PCs through a USB-serial adapter. " +
                        "Device Manager lists the adapter's COM port under Ports (COM & LPT). " +
                        "Calculators that use a USB cable directly are found by Scan.");
        help.Style = (Style)Application.Current.Resources["SecondaryCaptionTextStyle"];
        panel.Children.Add(help);

        var dialog = new ContentDialog
        {
            Title = "Set up a connection",
            Content = panel,
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        void Update() => dialog.IsPrimaryButtonEnabled = cableBox.SelectedIndex >= 0 && modelBox.SelectedIndex >= 0;
        cableBox.SelectionChanged += (_, _) => Update();
        modelBox.SelectionChanged += (_, _) => Update();
        Update();
        if (await ShowAsync(dialog) != ContentDialogResult.Primary || cableBox.SelectedIndex < 0 || modelBox.SelectedIndex < 0)
        {
            return null;
        }
        return (cableBox.SelectedIndex, modelBox.SelectedIndex);
    }

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            if (window.XamlRoot is not { } root)
            {
                return ContentDialogResult.None;
            }
            dialog.XamlRoot = root;
            dialog.RequestedTheme = window.ActualTheme;
            dialog.Style ??= (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TextBlock Wrap(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        MaxWidth = 480,
    };
}
