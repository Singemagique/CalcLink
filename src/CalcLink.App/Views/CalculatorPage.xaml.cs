using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace CalcLink.App.Views;

public sealed partial class CalculatorPage : Page
{
    private bool _internalDrag;

    public CalculatorPage()
    {
        ViewModel = App.GetService<CalculatorViewModel>();
        InitializeComponent();

        var source = new CollectionViewSource { IsSourceGrouped = true, Source = ViewModel.Groups };
        VariablesList.ItemsSource = source.View;
    }

    public CalculatorViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OnNavigatedToAsync();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection(VariablesList.SelectedItems.OfType<VariableItem>().ToList());

    /// <summary>Right-clicking an unselected row selects it, like Explorer.</summary>
    private void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: VariableItem item } && !VariablesList.SelectedItems.Contains(item))
        {
            VariablesList.SelectedItems.Clear();
            VariablesList.SelectedItems.Add(item);
        }
    }

    // --- Drag in from Explorer → SendFilesAsync ---------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_internalDrag || !ViewModel.CanSend || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"Send to {ViewModel.Connection.ModelName}";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_internalDrag || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        List<string> paths;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            // Dropped folders: send the files directly inside them (not recursive).
            foreach (var folder in items.OfType<StorageFolder>())
            {
                if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path))
                {
                    paths.AddRange(Directory.EnumerateFiles(folder.Path));
                }
            }
        }
        finally
        {
            deferral.Complete();
        }
        await ViewModel.SendPathsAsync(paths);
    }

    // --- Drag out to Explorer: receive into a temp folder on demand --------------------------------

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<VariableItem>().ToList();
        if (items.Count == 0 || !ViewModel.CanReceive)
        {
            e.Cancel = true;
            return;
        }
        _internalDrag = true;
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties.Title = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        // Deferred: the transfer only happens if the drop target (Explorer) actually asks for files.
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, request => ProvideFiles(request, items));
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _internalDrag = false;

    private async void ProvideFiles(DataProviderRequest request, IReadOnlyList<VariableItem> items)
    {
        var deferral = request.GetDeferral();
        try
        {
            var paths = await ViewModel.ReceiveToTempAsync(items);
            var files = new List<IStorageItem>();
            foreach (var path in paths)
            {
                files.Add(await StorageFile.GetFileFromPathAsync(path));
            }
            request.SetData(files);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Drag-out failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
