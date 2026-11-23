using System.Collections.ObjectModel;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.App.Views;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.ViewModels;

/// <summary>One line of the Files page.</summary>
public sealed class FileItemRow(GroupFileItem item)
{
    public GroupFileItem Item { get; } = item;

    public string Name => Item.Name.Length > 0 ? Item.Name : "(no name)";

    public string TypeText => VariableTypeNames.Of(Item.TypeCode, Item.IsApp);

    public string SizeText => Format.Bytes(Item.Size);

    /// <summary>Where the item was when the file was made.</summary>
    public string LocationText => Item.IsApp ? "Flash" : Item.Entry is null ? string.Empty : Item.Archived ? "Archive" : "RAM";

    public string AutomationName => $"{Name}, {TypeText}, {SizeText}" + (LocationText.Length > 0 ? $", {LocationText}" : string.Empty);
}

/// <summary>
/// The Files page (F5): open a TI group or backup (.tig), a group file (.8xg) or a variable file, see
/// what it holds without a calculator, read programs, save items on their own or as a new group, and
/// send the chosen ones to the connected calculator.
/// </summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private readonly PickerService _pickers;
    private readonly DialogService _dialogs;
    private readonly NotificationService _notifications;
    private readonly OperationService _operations;
    private readonly CalculatorViewModel _calculator;
    private IReadOnlyList<FileItemRow> _selection = [];
    private string? _workFolder;

    public FilesViewModel(PickerService pickers, DialogService dialogs, NotificationService notifications,
        OperationService operations, ConnectionService connection, CalculatorViewModel calculator)
    {
        _pickers = pickers;
        _dialogs = dialogs;
        _notifications = notifications;
        _operations = operations;
        _calculator = calculator;
        Connection = connection;
        connection.SessionChanged += (_, _) => UpdateState();
        operations.BusyChanged += (_, _) => UpdateState();
    }

    public ConnectionService Connection { get; }

    public ObservableCollection<FileItemRow> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(ShowEmpty), nameof(FileName))]
    public partial string? FilePath { get; private set; }

    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    public bool HasFile => FilePath is not null;

    public bool ShowEmpty => FilePath is null;

    public string FileName => FilePath is null ? string.Empty : Path.GetFileName(FilePath);

    public string SelectionText => _selection.Count switch
    {
        0 => string.Empty,
        1 => "1 selected",
        int n => $"{n} selected",
    };

    public bool CanView => _selection.Count == 1 && _selection[0].Item.IsProgram;

    public bool CanSaveItems => _selection.Count > 0;

    public bool CanSaveGroup => _selection.Any(r => r.Item.Entry is not null);

    public bool CanSend => _selection.Count > 0 && _calculator.CanSend;

    public string SendTip => Connection.IsConnected
        ? $"Send the selected items to the {Connection.ModelName}"
        : "Connect a calculator on the Devices page to send items";

    public void SetSelection(IReadOnlyList<FileItemRow> selection)
    {
        _selection = selection;
        UpdateState();
    }

    private void UpdateState()
    {
        OnPropertyChanged(string.Empty);
        ViewCommand.NotifyCanExecuteChanged();
        SaveItemsCommand.NotifyCanExecuteChanged();
        SaveGroupCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
    }

    // --- Open -----------------------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        string? path;
        try
        {
            path = await _pickers.PickFileAsync(GroupFile.Extensions);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the file picker", ex);
            return;
        }
        if (path is not null)
        {
            await LoadAsync(path);
        }
    }

    /// <summary>Opens <paramref name="path"/> (also used by Tools → Backups → Browse contents).</summary>
    public async Task LoadAsync(string path)
    {
        string work = Path.Combine(Path.GetTempPath(), "CalcLink", "files", Guid.NewGuid().ToString("N"));
        GroupContents contents;
        try
        {
            contents = await Task.Run(() => GroupFile.Open(path, work));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(work);
            await _dialogs.ShowMessageAsync("Can't open this file", $"{Path.GetFileName(path)}: {ex.Message}");
            return;
        }

        string? previous = _workFolder;
        _workFolder = work;
        TryDeleteFolder(previous);

        Items.Clear();
        foreach (var item in contents.Items
                     .OrderBy(i => i.IsApp)
                     .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(new FileItemRow(item));
        }
        int apps = contents.Items.Count(i => i.IsApp);
        int programs = contents.Items.Count(i => i.IsProgram);
        SummaryText = $"{contents.Items.Count} item{(contents.Items.Count == 1 ? "" : "s")} · {Format.Bytes(contents.TotalSize)}"
                      + (programs > 0 ? $" · {programs} program{(programs == 1 ? "" : "s")}" : string.Empty)
                      + (apps > 0 ? $" · {apps} app{(apps == 1 ? "" : "s")}" : string.Empty);
        FilePath = path;
        _selection = [];
        UpdateState();
    }

    // --- View -----------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanView))]
    private async Task ViewAsync()
    {
        if (_selection.Count != 1 || _selection[0].Item.Entry?.ProgramTokens is not { } tokens)
        {
            return;
        }
        var item = _selection[0];
        var listing = ProgramListing.From(item.Name, tokens);
        string details = $"{item.TypeText} · from {FileName}";
        var viewer = new ProgramViewerDialog(listing, details, suggested =>
            _pickers.PickSaveFileAsync(Path.GetFileNameWithoutExtension(suggested), [new("Text file (.txt)", [".txt"])]))
        {
            CanEdit = true,
        };
        await _dialogs.ShowAsync(viewer);
        if (viewer.EditRequested)
        {
            var editor = App.GetService<EditorViewModel>();
            if (await editor.LoadAsync(item.Name, tokens, item.Item.Entry?.TypeId == 0x06, $"From {FileName}"))
            {
                App.GetService<NavigationService>().Navigate(AppPage.Editor);
            }
        }
    }

    // --- Save -----------------------------------------------------------------------------------

    /// <summary>Saves each selected item as a file of its own (NAME.8xp, ...), as TI Connect would.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveItems))]
    private async Task SaveItemsAsync()
    {
        var chosen = _selection.ToList();
        string? folder;
        try
        {
            folder = await _pickers.PickFolderAsync();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the folder picker", ex);
            return;
        }
        if (folder is null)
        {
            return;
        }
        try
        {
            var taken = CalcFileNames.NewPathSet();
            foreach (var row in chosen)
            {
                string target = CalcFileNames.MakeUnique(folder, Path.GetFileName(row.Item.FilePath), taken);
                File.Copy(row.Item.FilePath, target, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("Couldn't save the files", ex);
            return;
        }
        _notifications.Post("Saved",
            chosen.Count == 1 ? $"Saved {chosen[0].Name} to {folder}." : $"Saved {chosen.Count} files to {folder}.",
            InfoBarSeverity.Success);
    }

    /// <summary>Saves the selected variables together as a TI-83 Plus group file (.8xg).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveGroup))]
    private async Task SaveGroupAsync()
    {
        var entries = _selection.Select(r => r.Item.Entry).OfType<TiVarEntry>().ToList();
        int left = _selection.Count - entries.Count;
        string? path;
        try
        {
            path = await _pickers.PickSaveFileAsync(Path.GetFileNameWithoutExtension(FileName) + " group",
                [new("TI-83 Plus / TI-84 Plus group (.8xg)", [".8xg"])]);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the save dialog", ex);
            return;
        }
        if (path is null)
        {
            return;
        }
        try
        {
            await File.WriteAllBytesAsync(path, TiVarFile.Write(entries, "Group created by CalcLink"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _dialogs.ShowErrorAsync("Couldn't save the group", ex);
            return;
        }
        _notifications.Post("Group saved",
            $"Saved {entries.Count} variable{(entries.Count == 1 ? "" : "s")} to {Path.GetFileName(path)}." +
            (left > 0 ? $" {left} app{(left == 1 ? "" : "s")} or other item{(left == 1 ? "" : "s")} can't go in a group and {(left == 1 ? "was" : "were")} left out." : string.Empty),
            left > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    // --- Send -----------------------------------------------------------------------------------

    /// <summary>Sends the selected items through the Calculator page's send path (name clashes ask first).</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync()
    {
        var paths = _selection.Select(r => r.Item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return _calculator.SendPathsAsync(paths);
    }

    private static void TryDeleteFolder(string? folder)
    {
        if (folder is null)
        {
            return;
        }
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp files; Windows cleans %TEMP% eventually.
        }
    }
}
