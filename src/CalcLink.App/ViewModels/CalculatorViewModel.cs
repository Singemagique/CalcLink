using System.Collections.ObjectModel;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.ViewModels;

/// <summary>Variables browser: list grouped by folder plus all variable commands.</summary>
public sealed partial class CalculatorViewModel : ObservableObject
{
    private readonly OperationService _operations;
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly WindowService _window;
    private IReadOnlyList<VariableItem> _selection = [];
    private bool _stale = true;

    public CalculatorViewModel(ConnectionService connection, OperationService operations, DialogService dialogs,
        PickerService pickers, SettingsService settings, NotificationService notifications, WindowService window,
        PendingFilesService pending)
    {
        Connection = connection;
        Pending = pending;
        _operations = operations;
        _dialogs = dialogs;
        _pickers = pickers;
        _settings = settings;
        _notifications = notifications;
        _window = window;

        _sortKey = settings.Current.VariableSort;
        _sortDescending = settings.Current.VariableSortDescending;
        _grouping = settings.Current.VariableGrouping;

        connection.SessionChanged += (_, _) =>
        {
            _all = [];
            Groups.Clear();
            _selection = [];
            _stale = true;
            IsEmpty = false;
            UpdateState();
        };
        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionService.Info))
            {
                OnPropertyChanged(nameof(MemoryText));
            }
        };
        operations.BusyChanged += (_, _) => UpdateState();
        settings.Changed += (_, _) => UpdateState();
    }

    public ConnectionService Connection { get; }

    /// <summary>Files from the command line waiting to be sent.</summary>
    public PendingFilesService Pending { get; }

    /// <summary>Source for the grouped CollectionViewSource.</summary>
    public ObservableCollection<VariableGroup> Groups { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The calculator has variables, but the Show filter / search hides all of them.</summary>
    [ObservableProperty]
    public partial bool IsFilteredEmpty { get; private set; }

    // --- Sorting, grouping, filtering ------------------------------------------------------------

    private IReadOnlyList<CalcVariable> _all = [];
    private VariableSortKey _sortKey;
    private bool _sortDescending;
    private VariableGrouping _grouping;
    private VariableCategory? _show;
    private string _search = string.Empty;

    /// <summary>Choices for the Sort box, in <see cref="VariableSortKey"/> order.</summary>
    public IReadOnlyList<string> SortChoices { get; } = ["Name", "Type", "Size", "Location"];

    /// <summary>Choices for the Group box, in <see cref="VariableGrouping"/> order.</summary>
    public IReadOnlyList<string> GroupChoices { get; } = ["Folder", "Type"];

    /// <summary>Choices for the Show box: "All" then every <see cref="VariableCategory"/>.</summary>
    public IReadOnlyList<string> ShowChoices { get; } =
        ["All items", .. Enum.GetValues<VariableCategory>().Select(VariableList.CategoryTitle)];

    public int SortIndex
    {
        get => (int)_sortKey;
        set => SetSort((VariableSortKey)Math.Clamp(value, 0, SortChoices.Count - 1), _sortDescending);
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set => SetSort(_sortKey, value);
    }

    public int GroupIndex
    {
        get => (int)_grouping;
        set
        {
            var grouping = (VariableGrouping)Math.Clamp(value, 0, GroupChoices.Count - 1);
            if (grouping == _grouping)
            {
                return;
            }
            _grouping = grouping;
            _settings.Update(s => s.VariableGrouping = grouping);
            OnPropertyChanged();
            Rebuild();
        }
    }

    public int ShowIndex
    {
        get => _show is { } c ? (int)c + 1 : 0;
        set
        {
            VariableCategory? show = value <= 0 ? null : (VariableCategory)Math.Min(value - 1, ShowChoices.Count - 2);
            if (show == _show)
            {
                return;
            }
            _show = show;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public string SearchText
    {
        get => _search;
        set
        {
            value ??= string.Empty;
            if (value == _search)
            {
                return;
            }
            _search = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public string SortDirectionText => _sortDescending ? "Descending" : "Ascending";

    // --- View menu (RadioMenuFlyoutItems: IsChecked is OneWay, clicks go through the Set* commands) ---

    public bool IsGroupByFolder => _grouping == VariableGrouping.Folder;
    public bool IsGroupByType => _grouping == VariableGrouping.Category;
    public bool IsSortByName => _sortKey == VariableSortKey.Name;
    public bool IsSortByType => _sortKey == VariableSortKey.Type;
    public bool IsSortBySize => _sortKey == VariableSortKey.Size;
    public bool IsSortByLocation => _sortKey == VariableSortKey.Location;
    public bool IsAscending => !_sortDescending;
    public bool IsDescending => _sortDescending;

    /// <summary>"Group by Folder" only makes sense on models with folders; elsewhere the same choice reads "None".</summary>
    public bool ShowFolderGrouping => Connection.Supports(CalcFeatures.Folders);
    public bool ShowNoGrouping => !ShowFolderGrouping;

    /// <summary>Tooltip / accessible name of the View button: the current grouping and sort.</summary>
    public string ViewSummary =>
        $"View: group by {(IsGroupByType ? "type" : ShowFolderGrouping ? "folder" : "none")}, " +
        $"sort by {SortChoices[(int)_sortKey].ToLowerInvariant()} ({SortDirectionText.ToLowerInvariant()})";

    [RelayCommand]
    private void SetGrouping(string grouping)
    {
        if (Enum.TryParse<VariableGrouping>(grouping, out var g))
        {
            GroupIndex = (int)g;
        }
    }

    [RelayCommand]
    private void SetSortKey(string key)
    {
        if (Enum.TryParse<VariableSortKey>(key, out var k))
        {
            SetSort(k, _sortDescending);
        }
    }

    [RelayCommand]
    private void SetSortDirection(string direction) =>
        SetSort(_sortKey, string.Equals(direction, "Descending", StringComparison.OrdinalIgnoreCase));

    public string SortDirectionGlyph => _sortDescending ? "" : ""; // Down / Up arrows

    // Column-header arrows (empty for columns that aren't the sort key).
    public string NameSortGlyph => HeaderGlyph(VariableSortKey.Name);
    public string TypeSortGlyph => HeaderGlyph(VariableSortKey.Type);
    public string SizeSortGlyph => HeaderGlyph(VariableSortKey.Size);
    public string LocationSortGlyph => HeaderGlyph(VariableSortKey.Location);

    private string HeaderGlyph(VariableSortKey key) => key != _sortKey ? string.Empty : SortDirectionGlyph;

    /// <summary>Column header click: sort by that column, or flip the direction if it already is.</summary>
    [RelayCommand]
    private void SortByColumn(string key)
    {
        if (!Enum.TryParse<VariableSortKey>(key, out var sortKey))
        {
            return;
        }
        SetSort(sortKey, sortKey == _sortKey ? !_sortDescending : false);
    }

    private void SetSort(VariableSortKey key, bool descending)
    {
        if (key == _sortKey && descending == _sortDescending)
        {
            return;
        }
        _sortKey = key;
        _sortDescending = descending;
        _settings.Update(s =>
        {
            s.VariableSort = key;
            s.VariableSortDescending = descending;
        });
        OnPropertyChanged(nameof(SortIndex));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(SortDirectionText));
        OnPropertyChanged(nameof(SortDirectionGlyph));
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(TypeSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(LocationSortGlyph));
        Rebuild();
    }

    /// <summary>Re-applies sort / grouping / filter to the last variable list read from the calculator.</summary>
    private void Rebuild()
    {
        Groups.Clear();
        var options = new VariableListOptions(_sortKey, _sortDescending, _grouping, _show, _search);
        foreach (var group in VariableList.Build(_all, options))
        {
            Groups.Add(new VariableGroup(group.Title, group.Items.Select(v => new VariableItem(v)),
                isFolder: _grouping == VariableGrouping.Folder));
        }
        IsEmpty = !_stale && _all.Count == 0;
        IsFilteredEmpty = _all.Count > 0 && Groups.Count == 0;
        _selection = [];
        UpdateState();
    }

    public string MemoryText
    {
        get
        {
            if (Connection.Info is not { } info)
            {
                return string.Empty;
            }
            var parts = new List<string>();
            if (info.RamUser > 0 || info.RamFree > 0)
            {
                parts.Add($"RAM: {Format.Bytes(info.RamFree)} free of {Format.Bytes(info.RamUser)}");
            }
            if (info.FlashUser > 0 || info.FlashFree > 0)
            {
                parts.Add($"Archive: {Format.Bytes(info.FlashFree)} free of {Format.Bytes(info.FlashUser)}");
            }
            return string.Join("   ·   ", parts);
        }
    }

    public int SelectionCount => _selection.Count;

    public string SelectionText => _selection.Count switch
    {
        0 => string.Empty,
        1 => "1 item selected",
        var n => $"{n} items selected",
    };

    // --- Feature gating --------------------------------------------------------------------------

    private bool Idle => Connection.IsConnected && !_operations.IsBusy;
    private bool HasSelection => _selection.Count > 0;
    private bool SingleSelection => _selection.Count == 1;

    // Commands the connected model can't do at all are hidden (Show*), not disabled: WinUI never shows
    // tooltips on disabled controls, so a "not supported" tip would never be seen. Can* only reflects
    // the connection, busy state and the selection.

    public bool ShowRename => Connection.Supports(CalcFeatures.Rename);
    public bool ShowArchive => Connection.Supports(CalcFeatures.ChangeAttr);
    public bool ShowNewFolder => Connection.Supports(CalcFeatures.Folders) && Connection.Supports(CalcFeatures.NewFolder);

    // TODO(contract): there is no CalcFeatures flag for remote execution; ModelNames approximates it.
    public bool ShowRun => Connection.Session is { } s && ModelNames.SupportsRemoteExecute(s.Model);

    public bool CanRefresh => Idle && Connection.Supports(CalcFeatures.DirList);
    public bool CanSend => Idle && (Connection.Supports(CalcFeatures.Vars) || Connection.Supports(CalcFeatures.Flash));
    public bool CanReceive => Idle && HasSelection && Connection.Supports(CalcFeatures.Vars);
    public bool CanDelete => Idle && HasSelection && Connection.Supports(CalcFeatures.DeleteVar);
    public bool CanRename => Idle && SingleSelection && ShowRename;

    /// <summary>Archive: something selected is in RAM. (Apps always live in flash; they never move.)</summary>
    public bool CanArchive => Idle && ShowArchive && _selection.Any(i => !i.Variable.IsApp && !i.IsArchived);

    /// <summary>Move to RAM: something selected is archived.</summary>
    public bool CanUnarchive => Idle && ShowArchive && _selection.Any(i => !i.Variable.IsApp && i.IsArchived);

    public bool CanNewFolder => Idle && ShowNewFolder;

    /// <summary>Run: exactly one program or app (not an AppVar) is selected.</summary>
    public bool CanRun => Idle && ShowRun && SingleSelection && IsRunnable(_selection[0].Variable);

    private static bool IsRunnable(CalcVariable v) => v.IsApp || VariableList.CategoryOf(v) == VariableCategory.Programs;

    public string RefreshTip => Connection.FeatureTip(CalcFeatures.DirList, "Refresh (F5)");
    public string SendTip => Connection.FeatureTip(Connection.Supports(CalcFeatures.Flash) ? CalcFeatures.Flash : CalcFeatures.Vars, "Send files to the calculator (Ctrl+O)");
    public string ReceiveTip => Connection.FeatureTip(CalcFeatures.Vars, "Receive the selected items to a folder you pick (Ctrl+S)");
    public string DeleteTip => Connection.FeatureTip(CalcFeatures.DeleteVar, "Delete the selected items (Del)");
    public string RenameTip => "Rename the selected item (F2)";
    public string ArchiveTip => "Archive: move the selected items to archive (flash) memory";
    public string UnarchiveTip => "Move to RAM: move the selected items out of the archive";
    public string NewFolderTip => "New folder (Ctrl+Shift+N)";
    public string RunTip => "Run the selected program or app on the calculator";

    private string? DefaultFolder => _settings.Current.DefaultReceiveFolder is { Length: > 0 } f ? f : null;

    public bool HasDefaultFolder => DefaultFolder is not null;

    /// <summary>Second action of the Receive split button.</summary>
    public string ReceiveToDefaultText => DefaultFolder is { } folder
        ? $"Receive to {ShortFolderName(folder)}"
        : "Set default folder…";

    public string ReceiveToDefaultTip => DefaultFolder is { } folder
        ? $"Receive the selected items to the default folder, {folder}"
        : "Choose a default receive folder and receive the selected items there";

    private static string ShortFolderName(string folder)
    {
        string trimmed = folder.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? $"“{name}”" : folder; // a drive root like "D:\" has no file name
    }

    public void SetSelection(IReadOnlyList<VariableItem> selection)
    {
        _selection = selection;
        UpdateState();
    }

    private void UpdateState()
    {
        // Re-evaluates every Can*/Tip/Title binding at once.
        OnPropertyChanged(string.Empty);
        RefreshCommand.NotifyCanExecuteChanged();
        SendFilesCommand.NotifyCanExecuteChanged();
        ReceiveCommand.NotifyCanExecuteChanged();
        ReceiveToDefaultCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        UnarchiveCommand.NotifyCanExecuteChanged();
        NewFolderCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    public async Task OnNavigatedToAsync()
    {
        UpdateState();
        if (_stale && CanRefresh)
        {
            await RefreshAsync();
        }
        if (Pending.HasPending && CanSend)
        {
            await SendPathsAsync(Pending.Take());
        }
    }

    // --- Commands --------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        var (outcome, vars) = await _operations.RunAsync("Reading the variable list",
            async (_, ct) =>
            {
                var list = await session.GetVariablesAsync(ct);
                await Connection.RefreshInfoAsync(ct);
                return list;
            });
        if (outcome != OperationOutcome.Succeeded || vars is null)
        {
            return;
        }

        _stale = false;
        _all = vars;
        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendFilesAsync()
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = await _pickers.PickFilesAsync();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not open the file picker", ex);
            return;
        }
        await SendPathsAsync(paths);
    }

    /// <summary>Sends local files (picker or drag &amp; drop from Explorer).</summary>
    public async Task SendPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || Connection.Session is not { } session || !CanSend)
        {
            return;
        }

        // OS files have their own guarded flow on the Tools page.
        if (Connection.Service is { } service
            && paths.Any(p => SafeClassify(service, p) == TiFileClass.Os))
        {
            await _dialogs.ShowMessageAsync("Operating system file",
                "One of these files is a calculator operating system. Use Tools → Send OS… so CalcLink can check the battery and warn you before upgrading.");
            return;
        }

        paths = await ResolveConflictsAsync(session, paths);
        if (paths.Count == 0)
        {
            return;
        }

        string what = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";
        var outcome = await _operations.RunAsync($"Sending {what}",
            (progress, ct) => session.SendFilesAsync(paths, progress, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            _notifications.Post("Sent", $"Sent {what} to the calculator.", InfoBarSeverity.Success);
        }
        if (outcome is OperationOutcome.Succeeded or OperationOutcome.Failed or OperationOutcome.Cancelled)
        {
            await RefreshIfPossibleAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanReceive))]
    private async Task ReceiveAsync()
    {
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
        if (folder is not null)
        {
            await ReceiveToAsync(folder);
        }
    }

    /// <summary>Receive to the default folder; when none is set, pick one, save it as the default, then receive.</summary>
    [RelayCommand(CanExecute = nameof(CanReceive))]
    private async Task ReceiveToDefaultAsync()
    {
        string? folder = DefaultFolder;
        if (folder is null)
        {
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
            _settings.Update(s => s.DefaultReceiveFolder = folder);
        }
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Default receive folder is not usable", ex);
            return;
        }
        await ReceiveToAsync(folder);
    }

    private async Task ReceiveToAsync(string folder)
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        var vars = _selection.Select(i => i.Variable).ToList();
        string what = vars.Count == 1 ? vars[0].Name : $"{vars.Count} items";
        var (outcome, files) = await _operations.RunAsync($"Receiving {what}",
            (progress, ct) => session.ReceiveAsync(vars, folder, progress, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            _notifications.Post("Received", $"Saved {files?.Count ?? 0} file(s) to {folder}.", InfoBarSeverity.Success);
        }
    }

    /// <summary>
    /// Drag-out to Explorer: receives <paramref name="items"/> into a fresh temp folder and returns the
    /// written paths. May be called off the UI thread (from a DataProvider callback).
    /// </summary>
    public Task<IReadOnlyList<string>> ReceiveToTempAsync(IReadOnlyList<VariableItem> items) =>
        _window.DispatcherQueue.EnqueueAsync<IReadOnlyList<string>>(async () =>
        {
            if (Connection.Session is not { } session || items.Count == 0)
            {
                return [];
            }
            string folder = Path.Combine(Path.GetTempPath(), "CalcLink", "drag", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var vars = items.Select(i => i.Variable).ToList();
            var (outcome, files) = await _operations.RunAsync($"Receiving {vars.Count} item(s) for drag and drop",
                (progress, ct) => session.ReceiveAsync(vars, folder, progress, ct));
            return outcome == OperationOutcome.Succeeded && files is not null ? files : [];
        });

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        var vars = _selection.Select(i => i.Variable).ToList();
        string what = vars.Count == 1 ? $"“{vars[0].FullName}”" : $"{vars.Count} items";
        if (!await _dialogs.ConfirmAsync("Delete from calculator?",
                $"Delete {what} from the {Connection.ModelName}? This cannot be undone.", "Delete", destructive: true))
        {
            return;
        }
        await _operations.RunAsync($"Deleting {what}", (_, ct) => session.DeleteAsync(vars, ct));
        await RefreshIfPossibleAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        if (Connection.Session is not { } session || _selection.Count != 1)
        {
            return;
        }
        var v = _selection[0].Variable;
        var result = await _dialogs.PromptRenameAsync(v.Folder, v.Name, Connection.Supports(CalcFeatures.Folders));
        if (result is not { } r || (r.Name == v.Name && r.Folder == v.Folder))
        {
            return;
        }
        await _operations.RunAsync($"Renaming {v.Name}", (_, ct) => session.RenameAsync(v, r.Folder, r.Name, ct));
        await RefreshIfPossibleAsync();
    }

    [RelayCommand(CanExecute = nameof(CanArchive))]
    private Task ArchiveAsync() => SetAttributeAsync(VariableAttribute.Archived, "Archiving");

    [RelayCommand(CanExecute = nameof(CanUnarchive))]
    private Task UnarchiveAsync() => SetAttributeAsync(VariableAttribute.None, "Moving to RAM");

    private async Task SetAttributeAsync(VariableAttribute attribute, string verb)
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        // Archive acts on what's in RAM; Move to RAM only on what's archived. (Comparing with
        // `Attribute != None` would also "move to RAM" locked/protected RAM items, i.e. unlock them.)
        bool toArchive = attribute == VariableAttribute.Archived;
        var vars = _selection.Where(i => !i.Variable.IsApp && i.IsArchived != toArchive).Select(i => i.Variable).ToList();
        if (vars.Count == 0)
        {
            return;
        }
        await _operations.RunAsync($"{verb} {vars.Count} item(s)", async (progress, ct) =>
        {
            for (int i = 0; i < vars.Count; i++)
            {
                progress.Report(new TransferProgress(i, vars.Count, 0, $"{verb} {vars[i].Name}"));
                await session.SetAttributeAsync(vars[i], attribute, ct);
            }
        });
        await RefreshIfPossibleAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNewFolder))]
    private async Task NewFolderAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        var name = await _dialogs.PromptAsync("New folder", "Folder name", string.Empty, "Create");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        await _operations.RunAsync($"Creating folder {name}", (_, ct) => session.CreateFolderAsync(name, ct));
        await RefreshIfPossibleAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (Connection.Session is not { } session || _selection.Count != 1)
        {
            return;
        }
        var v = _selection[0].Variable;
        await _operations.RunAsync($"Running {v.Name}", (_, ct) => session.ExecuteAsync(v, ct));
    }

    /// <summary>
    /// Asks before sending files whose variables already exist on the calculator. Returns the paths to
    /// send: all of them (Replace), those without conflicts (Skip), or none (Cancel).
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveConflictsAsync(ICalculatorSession session, IReadOnlyList<string> paths)
    {
        if (_stale && CanRefresh)
        {
            await RefreshAsync(); // compare against the calculator's current contents
        }
        if (_stale)
        {
            return paths; // couldn't read the list: nothing to compare with
        }

        var incoming = paths.Select(p => (p, session.GetFileVariables(p))).ToList();
        IReadOnlyList<VariableConflict> conflicts = VariableConflicts.Find(session.Model, _all, incoming);
        if (conflicts.Count == 0)
        {
            return paths;
        }

        const int Shown = 8;
        var lines = conflicts.Take(Shown)
            .Select(c => $"• {c.Existing.FullName}  ({VariableTypeNames.Of(c.Existing)}{(c.Existing.Attribute == VariableAttribute.Archived ? ", in archive" : string.Empty)})")
            .ToList();
        if (conflicts.Count > Shown)
        {
            lines.Add($"• …and {conflicts.Count - Shown} more");
        }
        bool one = conflicts.Count == 1;
        var conflictPaths = conflicts.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // "Skip" only makes sense when some files don't clash.
        bool canSkip = paths.Any(p => !conflictPaths.Contains(p));
        var dialog = new ContentDialog
        {
            Title = one ? "Replace the variable on the calculator?" : $"Replace {conflicts.Count} variables on the calculator?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = (one ? "The calculator already has a variable with this name:" : "The calculator already has variables with these names:")
                       + "\n\n" + string.Join("\n", lines) + "\n\n"
                       + (one ? "Replace overwrites it with the copy being sent." : "Replace overwrites them with the copies being sent.")
                       + (canSkip ? " Skip sends only the files that don't clash." : string.Empty),
            },
            PrimaryButtonText = "Replace",
            SecondaryButtonText = canSkip ? "Skip" : string.Empty,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await _dialogs.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => paths,
            ContentDialogResult.Secondary => paths.Where(p => !conflictPaths.Contains(p)).ToList(),
            _ => [],
        };
    }

    private async Task RefreshIfPossibleAsync()
    {
        if (CanRefresh)
        {
            await RefreshAsync();
        }
        else
        {
            _stale = true;
        }
    }

    private static TiFileClass SafeClassify(ICalculatorService service, string path)
    {
        try
        {
            return service.ClassifyFile(path, out _);
        }
        catch (Exception)
        {
            return TiFileClass.Unknown;
        }
    }
}
