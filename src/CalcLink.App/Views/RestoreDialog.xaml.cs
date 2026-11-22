using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.Views;

/// <summary>Lets the user pick which items of a backup to send back to the calculator.</summary>
public sealed partial class RestoreDialog : ContentDialog
{
    public RestoreDialog(RestoreSelectionViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public RestoreSelectionViewModel ViewModel { get; }
}
