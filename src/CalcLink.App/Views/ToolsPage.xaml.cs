using CalcLink.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace CalcLink.App.Views;

public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        ViewModel = App.GetService<ToolsViewModel>();
        InitializeComponent();
    }

    public ToolsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.OnNavigatedTo();
    }

    // Backup rows: x:Bind inside the item template can't reach the page's commands, so the buttons
    // carry their item in Tag.

    private void OnRestoreClick(object sender, RoutedEventArgs e) =>
        Run(ViewModel.Backups.RestoreCommand, sender);

    private void OnShowInFolderClick(object sender, RoutedEventArgs e) =>
        Run(ViewModel.Backups.ShowInFolderCommand, sender);

    private void OnDeleteBackupClick(object sender, RoutedEventArgs e) =>
        Run(ViewModel.Backups.DeleteCommand, sender);

    private static void Run(System.Windows.Input.ICommand command, object sender)
    {
        if (sender is FrameworkElement { Tag: BackupListItem item } && command.CanExecute(item))
        {
            command.Execute(item);
        }
    }
}
