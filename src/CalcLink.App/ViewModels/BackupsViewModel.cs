using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CalcLink.App.Helpers;
using CalcLink.App.Services;
using CalcLink.App.Views;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.ViewModels;

/// <summary>One dated backup in the list on the Tools page.</summary>
public sealed class BackupListItem(BackupFile file)
{
    public BackupFile File { get; } = file;

    public string Title => File.Created.ToString("D", CultureInfo.CurrentCulture) + ", " +
                           File.Created.ToString("t", CultureInfo.CurrentCulture);

    public string Details => File.IsTiGroup
        ? $"{(File.ItemCount is { } n ? $"{n} item{(n == 1 ? "" : "s")} · " : string.Empty)}{Format.Bytes(File.Size)}"
        : $"RAM backup ({Path.GetExtension(File.Path).TrimStart('.')}) · {Format.Bytes(File.Size)}";

    public string RestoreAutomationName => $"Restore the backup from {Title}";
    public string MoreAutomationName => $"More actions for the backup from {Title}";
}

/// <summary>
/// "Back up now" into a dated file per calculator, the list of past backups, and selective restore.
/// See docs/REVIEW.md F4. Backups are TI groups (.tig) on flash models: their native backup is a RAM
/// image without the archive or apps.
/// </summary>
public sealed partial class BackupsViewModel : ObservableObject
{
    private readonly OperationService _operations;
    private readonly DialogService _dialogs;
    private readonly PickerService _pickers;
    private readonly NotificationService _notifications;
    private readonly SettingsService _settings;
    private readonly CalculatorViewModel _calculator;

    public BackupsViewModel(ConnectionService connection, OperationService operations, DialogService dialogs,
        PickerService pickers, NotificationService notifications, SettingsService settings, CalculatorViewModel calculator)
    {
        Connection = connection;
        _operations = operations;
        _dialogs = dialogs;
        _pickers = pickers;
        _notifications = notifications;
        _settings = settings;
        _calculator = calculator;

        connection.SessionChanged += (_, _) => Reload();
        connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionService.Info))
            {
                Reload(); // the folder name includes the calculator ID, which arrives with the info
            }
        };
        operations.BusyChanged += (_, _) => UpdateState();
        settings.Changed += (_, _) =>
        {
            if (Root != _shownRoot)
            {
                Reload();
            }
        };
    }

    private string? _shownRoot;

    public ConnectionService Connection { get; }

    public ObservableCollection<BackupListItem> Items { get; } = [];

    public bool HasBackups => Items.Count > 0;

    public bool ShowNoBackups => Connection.IsConnected && Items.Count == 0;

    /// <summary>Root of all calculators' backups (Settings → Backups folder).</summary>
    public string Root => string.IsNullOrWhiteSpace(_settings.Current.BackupFolder) ? Backups.DefaultRoot : _settings.Current.BackupFolder!;

    /// <summary>This calculator's backups folder, or null when nothing is connected.</summary>
    public string? Folder => Connection.Session is { } s
        ? Backups.CalculatorFolder(Root, ModelNames.Of(s.Model), Connection.Info?.CalcId)
        : null;

    public string FolderText => Folder ?? string.Empty;

    private bool Idle => Connection.IsConnected && !_operations.IsBusy;

    /// <summary>The extension "Back up now" uses for the connected model, or null if it can't back up.</summary>
    private string? BackupExtension => Connection.Session is not { } s || !Connection.Supports(CalcFeatures.Backup)
        ? null
        : Backups.UsesTiGroup(s.Model) ? ".tig"
        : ModelNames.NativeBackupExtension(s.Model) is { } ext ? "." + ext
        : null;

    public bool CanBackUpNow => Idle && BackupExtension is not null;

    public bool CanSaveAs => Idle && Connection.Supports(CalcFeatures.Backup);

    public bool CanRestore => Idle;

    public string BackupNote =>
        !Connection.IsConnected ? "Connect a calculator to back it up or see its backups."
        : Connection.Supports(CalcFeatures.Backup) ? string.Empty
        : $"Backup isn't supported on the {Connection.ModelName}.";

    /// <summary>What a backup contains on this model, for the card's description.</summary>
    public string BackupDescription => Connection.Session is { } s && Backups.UsesTiGroup(s.Model)
        ? "Back up now saves every variable, program and app, in RAM and in the archive, to a dated TI group (.tig) file in this calculator's backups folder."
        : "Back up now saves the calculator's memory to a dated file in this calculator's backups folder.";

    public void OnNavigatedTo() => Reload();

    private void UpdateState()
    {
        OnPropertyChanged(string.Empty);
        BackUpNowCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-reads the connected calculator's backups folder.</summary>
    public void Reload()
    {
        _shownRoot = Root;
        Items.Clear();
        if (Folder is { } folder && Connection.Session is { } s)
        {
            var exts = new List<string> { ".tig" };
            if (ModelNames.NativeBackupExtension(s.Model) is { } native)
            {
                exts.Add("." + native);
            }
            try
            {
                foreach (var b in Backups.List(folder, exts))
                {
                    Items.Add(new BackupListItem(b));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder shows as empty; Back up now reports the actual error.
            }
        }
        UpdateState();
    }

    // --- Back up ---------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanBackUpNow))]
    private async Task BackUpNowAsync()
    {
        if (Connection.Session is not { } session || Folder is not { } folder || BackupExtension is not { } ext)
        {
            return;
        }
        string path;
        try
        {
            Directory.CreateDirectory(folder);
            path = Backups.NewBackupPath(folder, DateTime.Now, ext);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await _dialogs.ShowErrorAsync("Couldn't create the backups folder", ex);
            return;
        }

        var outcome = await _operations.RunAsync("Backing up the calculator",
            (progress, ct) => session.BackupAsync(path, progress, ct));
        Reload();
        if (outcome == OperationOutcome.Succeeded)
        {
            int? n = ext == ".tig" ? Backups.CountEntries(path) : null;
            _notifications.Post("Backup saved",
                n is { } count ? $"Saved {count} items to {Path.GetFileName(path)}." : $"Saved to {Path.GetFileName(path)}.",
                InfoBarSeverity.Success);
        }
    }

    /// <summary>The old flow: choose the file name and format (.tig or the native backup).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAsAsync()
    {
        if (Connection.Session is not { } session)
        {
            return;
        }
        var choices = new List<KeyValuePair<string, IList<string>>> { new("TI group (.tig)", [".tig"]) };
        if (ModelNames.NativeBackupExtension(session.Model) is { } ext)
        {
            choices.Add(new($"{Connection.ModelName} RAM backup (.{ext})", ["." + ext]));
        }
        string? path;
        try
        {
            path = await _pickers.PickSaveFileAsync($"{Connection.ModelName} backup {DateTime.Now:yyyy-MM-dd}", choices);
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
        var outcome = await _operations.RunAsync("Backing up the calculator",
            (progress, ct) => session.BackupAsync(path, progress, ct));
        if (outcome == OperationOutcome.Succeeded)
        {
            _notifications.Post("Backup complete", $"Saved to {path}.", InfoBarSeverity.Success);
            Reload();
        }
    }

    // --- Restore ---------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync(BackupListItem? item)
    {
        if (item is null || Connection.Session is not { } session || Connection.Service is not { } service)
        {
            return;
        }
        if (!item.File.IsTiGroup)
        {
            await RestoreRamImageAsync(session, item);
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), "CalcLink", "restore", Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Unpack the backup and read what each file holds.
            IReadOnlyList<string> files;
            try
            {
                files = Backups.ExtractTiGroup(item.File.Path, temp);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await _dialogs.ShowErrorAsync("Couldn't open the backup", ex);
                return;
            }
            var variables = new List<(string, IReadOnlyList<CalcVariable>)>();
            var apps = new List<(string, string, long)>();
            int unreadable = 0;
            foreach (string f in files)
            {
                try
                {
                    if (service.ClassifyFile(f, out _) == TiFileClass.App)
                    {
                        apps.Add((f, Path.GetFileNameWithoutExtension(f).Trim(), new FileInfo(f).Length));
                        continue;
                    }
                    var vars = session.GetFileVariables(f);
                    if (vars.Count == 0)
                    {
                        unreadable++;
                    }
                    else
                    {
                        variables.Add((f, vars));
                    }
                }
                catch (Exception ex) when (ex is CalcLinkException or IOException)
                {
                    unreadable++;
                }
            }

            // 2. Compare with what is on the calculator now (and read its free RAM).
            var (outcome, current) = await _operations.RunAsync("Reading the variable list",
                async (_, ct) =>
                {
                    var list = await session.GetVariablesAsync(ct);
                    await Connection.RefreshInfoAsync(ct);
                    return list;
                });
            if (outcome != OperationOutcome.Succeeded || current is null)
            {
                return;
            }
            var plan = RestorePlan.Build(session.Model, current, variables, apps);
            if (plan.Count == 0)
            {
                await _dialogs.ShowMessageAsync("Nothing to restore", "CalcLink couldn't read any items from this backup.");
                return;
            }

            // 3. Let the user choose.
            bool canArchive = Connection.Supports(CalcFeatures.ChangeAttr);
            var selection = new RestoreSelectionViewModel(plan,
                $"Backup from {item.Title}" + (unreadable > 0 ? $" ({unreadable} item(s) couldn't be read and aren't listed)" : string.Empty),
                Connection.ModelName, canArchive, Connection.Info?.RamFree ?? 0);
            if (await _dialogs.ShowAsync(new RestoreDialog(selection)) != ContentDialogResult.Primary)
            {
                return;
            }
            var chosen = selection.Selected;
            if (chosen.Count == 0)
            {
                return;
            }

            // 4. Send. Each file holds one variable or app; send a file once even if listed twice.
            var paths = chosen.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string what = chosen.Count == 1 ? chosen[0].Name : $"{chosen.Count} items";
            var sent = await _operations.RunAsync($"Restoring {what}",
                (progress, ct) => session.SendFilesAsync(paths, progress, ct));
            _calculator.MarkStale();
            if (sent != OperationOutcome.Succeeded)
            {
                return;
            }

            // 5. Put archived items back in the archive where the link allows it; otherwise list them.
            var inRam = await RearchiveAsync(session, chosen.Where(i => i.WasArchived && i.Variable is not null).ToList(), canArchive);
            _notifications.Post("Restore complete", $"Restored {what} from the backup.", InfoBarSeverity.Success);
            if (inRam.Count > 0)
            {
                const int Shown = 12;
                var lines = inRam.Take(Shown).Select(n => "• " + n).ToList();
                if (inRam.Count > Shown)
                {
                    lines.Add($"• …and {inRam.Count - Shown} more");
                }
                await _dialogs.ShowMessageAsync("Archive these on the calculator",
                    $"These were in the archive when the backup was made. The {Connection.ModelName} can't receive " +
                    "straight into the archive, so they are in RAM now:\n\n" + string.Join("\n", lines) + "\n\n" +
                    "To archive one on a TI-83 Plus or TI-84 Plus: press 2nd then + (MEM), choose Archive, " +
                    "then pick the item (programs are under PRGM) and press ENTER.");
            }
        }
        finally
        {
            TryDeleteFolder(temp);
        }
    }

    /// <summary>
    /// After a restore: archives what was archived in the backup (models that can do it over the link).
    /// Returns the names of restored, previously archived items that are in RAM now.
    /// </summary>
    private async Task<IReadOnlyList<string>> RearchiveAsync(ICalculatorSession session, IReadOnlyList<RestoreItem> wereArchived, bool canArchive)
    {
        if (wereArchived.Count == 0)
        {
            return [];
        }
        var (outcome, current) = await _operations.RunAsync("Reading the variable list",
            (_, ct) => session.GetVariablesAsync(ct));
        if (outcome != OperationOutcome.Succeeded || current is null)
        {
            return [];
        }
        // Where did each restored variable end up? (An archived variable that was overwritten stays archived.)
        var nowInRam = new List<CalcVariable>();
        foreach (var item in wereArchived)
        {
            var match = VariableConflicts.Find(session.Model, current, [(item.Path, [item.Variable!])]).FirstOrDefault();
            if (match is not null && match.Existing.Attribute != VariableAttribute.Archived)
            {
                nowInRam.Add(match.Existing);
            }
        }
        if (nowInRam.Count == 0 || !canArchive)
        {
            return nowInRam.Select(v => v.FullName).ToList();
        }
        var archived = await _operations.RunAsync("Archiving restored items", async (progress, ct) =>
        {
            for (int i = 0; i < nowInRam.Count; i++)
            {
                progress.Report(new TransferProgress(i, nowInRam.Count, 0, $"Archiving {nowInRam[i].FullName}"));
                await session.SetAttributeAsync(nowInRam[i], VariableAttribute.Archived, ct);
            }
        });
        return archived == OperationOutcome.Succeeded ? [] : nowInRam.Select(v => v.FullName).ToList();
    }

    /// <summary>A native backup (e.g. .8xb) is a RAM image: restoring it replaces all of RAM.</summary>
    private async Task RestoreRamImageAsync(ICalculatorSession session, BackupListItem item)
    {
        bool go = await _dialogs.ConfirmAsync("Restore this RAM backup?",
            $"This backup from {item.Title} is an image of the calculator's RAM. Restoring it replaces everything " +
            "in RAM with the backed-up contents; RAM variables made since then are lost.\n\n" +
            "It doesn't include the archive or apps. If the archive has changed since the backup, the calculator " +
            "may list archived items wrongly afterwards. A TI group (.tig) backup is safer to restore from.",
            "Replace RAM", destructive: true);
        if (!go)
        {
            return;
        }
        var outcome = await _operations.RunAsync("Restoring RAM backup",
            (progress, ct) => session.SendFilesAsync([item.File.Path], progress, ct));
        _calculator.MarkStale();
        if (outcome == OperationOutcome.Succeeded)
        {
            _notifications.Post("Restore complete", "The RAM backup was restored.", InfoBarSeverity.Success);
        }
    }

    // --- Files -----------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ShowInFolderAsync(BackupListItem? item)
    {
        if (item is not null)
        {
            await OpenExplorerAsync($"/select,\"{item.File.Path}\"");
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (Folder is not { } folder)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("Couldn't open the backups folder", ex);
            return;
        }
        await OpenExplorerAsync($"\"{folder}\"");
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupListItem? item)
    {
        if (item is null)
        {
            return;
        }
        bool go = await _dialogs.ConfirmAsync("Delete this backup?",
            $"The backup from {item.Title} moves to the Recycle Bin.", "Delete", destructive: true);
        if (!go)
        {
            return;
        }
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.File.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await _dialogs.ShowErrorAsync("Couldn't delete the backup", ex);
        }
        Reload();
    }

    private async Task OpenExplorerAsync(string args)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Couldn't open File Explorer", ex);
        }
    }

    private static void TryDeleteFolder(string folder)
    {
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
