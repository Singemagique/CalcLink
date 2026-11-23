using System.Text;
using CalcLink.App.Helpers;
using CalcLink.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace CalcLink.App.Views;

/// <summary>
/// Read-only view of one program (F1). TI-BASIC appears as the calculator's editor shows it, with line
/// numbers; assembly programs are labelled as such and shown as a hex dump.
/// </summary>
public sealed partial class ProgramViewerDialog : ContentDialog
{
    private readonly ProgramListing _listing;
    private readonly Func<string, Task<string?>> _pickSavePath;

    /// <param name="details">One line under the title, e.g. "Program (protected) · in Archive".</param>
    /// <param name="pickSavePath">Asks where to save; gets the suggested file name, returns a path or null.</param>
    public ProgramViewerDialog(ProgramListing listing, string details, Func<string, Task<string?>> pickSavePath)
    {
        _listing = listing;
        _pickSavePath = pickSavePath;
        InitializeComponent();

        Title = listing.Name;
        bool asm = listing.Kind == ProgramKind.Assembly;
        string size = Format.Bytes(listing.ByteCount);
        DetailsText.Text = asm
            ? $"{details} · assembly · {size}"
            : $"{details} · TI-BASIC · {size} · {listing.LineCount} line{(listing.LineCount == 1 ? "" : "s")}";

        AssemblyInfo.IsOpen = asm;
        AssemblyInfo.Message =
            $"{listing.Name} is machine code, not TI-BASIC, so it can't be shown as text. Below is its content in " +
            $"hexadecimal. It runs on the calculator itself: on a TI-83 Plus or TI-84 Plus with Asm(prgm{listing.Name}), " +
            "or from a shell such as MirageOS.";

        string[] lines = listing.Text.Split('\n');
        Code.Text = listing.Text.Length == 0 ? "(empty program)" : listing.Text;
        LineNumbers.Text = asm || listing.Text.Length == 0
            ? string.Empty
            : string.Join("\n", Enumerable.Range(1, lines.Length));
        LineNumbers.Visibility = LineNumbers.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Code, asm ? $"Hex dump of {listing.Name}" : $"Listing of {listing.Name}");
    }

    /// <summary>The text Copy and Save produce: the listing with Windows line endings.</summary>
    private string ExportText => _listing.Text.Replace("\n", "\r\n");

    private void OnCopyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // stay open
        var package = new DataPackage();
        package.SetText(ExportText);
        Clipboard.SetContent(package);
        StatusText.Text = _listing.Kind == ProgramKind.Basic ? "Copied the program to the clipboard." : "Copied the hex dump to the clipboard.";
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // stay open
        var deferral = args.GetDeferral();
        try
        {
            string? path = await _pickSavePath(_listing.Name + ".txt");
            if (path is null)
            {
                return;
            }
            // UTF-8 with BOM: the listing uses symbols such as → ² √ θ, and Notepad then shows them right.
            await File.WriteAllTextAsync(path, ExportText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = $"Saved to {path}.";
        }
        catch (Exception ex)
        {
            // File errors and picker failures alike: report here, never in a second dialog.
            StatusText.Text = $"Couldn't save: {ex.Message}";
        }
        finally
        {
            deferral.Complete();
        }
    }
}
