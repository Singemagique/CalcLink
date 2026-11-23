using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CalcLink.App.Views;

public sealed partial class FilesPage : Page
{
    public FilesPage()
    {
        ViewModel = App.GetService<FilesViewModel>();
        InitializeComponent();
    }

    public FilesViewModel ViewModel { get; }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection(ItemsList.SelectedItems.OfType<FileItemRow>().ToList());

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.ViewCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.ViewCommand.Execute(null);
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.ViewCommand.CanExecute(null))
        {
            e.Handled = true;
            ViewModel.ViewCommand.Execute(null);
        }
    }
}
