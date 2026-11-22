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
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Wrap(Format.Error(ex)));
        if (ex is CalcLinkException { Code: var code } && ex is not FeatureNotSupportedException)
        {
            panel.Children.Add(new TextBlock
            {
                Text = code > 0 ? $"tilibs error {code}" : $"CalcLink error {code}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.7,
                IsTextSelectionEnabled = true,
            });
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
