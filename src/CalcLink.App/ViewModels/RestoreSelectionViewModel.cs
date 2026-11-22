using System.Collections.ObjectModel;
using System.ComponentModel;
using CalcLink.App.Helpers;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalcLink.App.ViewModels;

/// <summary>One line of the restore dialog.</summary>
public sealed partial class RestoreRow : ObservableObject
{
    public RestoreRow(RestoreItem item)
    {
        Item = item;
        IsSelected = item.CanRestore && item.SelectedByDefault;
    }

    public RestoreItem Item { get; }

    public string Name => Item.Name;

    public string TypeText => Item.Variable is { } v ? VariableTypeNames.Of(v) : "App";

    public string SizeText => Format.Bytes(Item.Size);

    /// <summary>Where it was when the backup was made.</summary>
    public string LocationText => Item.IsApp ? "Flash" : Item.WasArchived ? "Archive" : "RAM";

    public string StatusText => Item.Status switch
    {
        RestoreStatus.Missing => "Not on calculator",
        RestoreStatus.OnCalculator => "On calculator",
        _ => "Installed",
    };

    public bool CanRestore => Item.CanRestore;

    public string AutomationName => $"{Name}, {TypeText}, {SizeText}, {StatusText}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>State of the restore dialog: which items are ticked, and what that will do.</summary>
public sealed partial class RestoreSelectionViewModel : ObservableObject
{
    private readonly bool _canArchive;
    private readonly ulong _ramFree;
    private readonly string _modelName;

    /// <param name="canArchive">The model can archive over the link, so archived items go back to the archive.</param>
    /// <param name="ramFree">Free RAM on the calculator in bytes; 0 if unknown.</param>
    public RestoreSelectionViewModel(IReadOnlyList<RestoreItem> items, string backupTitle, string modelName,
        bool canArchive, ulong ramFree)
    {
        BackupTitle = backupTitle;
        _modelName = modelName;
        _canArchive = canArchive;
        _ramFree = ramFree;
        foreach (var item in items.OrderBy(i => i.IsApp).ThenBy(i => i.Status).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new RestoreRow(item);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
    }

    public string BackupTitle { get; }

    public string IntroText =>
        $"{BackupTitle}. Items that aren't on the calculator are selected. Ticking an item that is on the " +
        "calculator replaces it with the backed-up copy.";

    public ObservableCollection<RestoreRow> Rows { get; } = [];

    public IReadOnlyList<RestoreItem> Selected => Rows.Where(r => r.IsSelected && r.CanRestore).Select(r => r.Item).ToList();

    public bool HasSelection => Rows.Any(r => r.IsSelected && r.CanRestore);

    public string SummaryText
    {
        get
        {
            var selected = Selected;
            if (selected.Count == 0)
            {
                return "Nothing selected.";
            }
            int replace = selected.Count(i => i.Status == RestoreStatus.OnCalculator);
            string text = $"{selected.Count} selected · {Format.Bytes(selected.Sum(i => i.Size))}";
            if (replace > 0)
            {
                text += replace == 1
                    ? " · 1 replaces an item on the calculator"
                    : $" · {replace} replace items on the calculator";
            }
            return text;
        }
    }

    /// <summary>Items that were archived will arrive in RAM (the model can't receive into the archive).</summary>
    public bool ShowArchiveNote => !_canArchive && Selected.Any(i => i.WasArchived);

    public string ArchiveNoteText =>
        $"The {_modelName} can't receive straight into the archive, so items that were archived arrive in RAM. " +
        "CalcLink lists them when the restore is done; archive them on the calculator afterwards.";

    /// <summary>The selection needs more RAM than the calculator reports free.</summary>
    public bool ShowRamWarning => !_canArchive && _ramFree > 0 && RestorePlan.RamNeeded(Selected) > (long)_ramFree;

    public string RamWarningText =>
        $"The selection needs about {Format.Bytes(RestorePlan.RamNeeded(Selected))} of RAM, but the calculator has " +
        $"{Format.Bytes(_ramFree)} free. Restore fewer items at a time, archiving them on the calculator in between.";

    [RelayCommand]
    private void SelectMissing()
    {
        foreach (var r in Rows)
        {
            r.IsSelected = r.CanRestore && r.Item.SelectedByDefault;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Rows)
        {
            r.IsSelected = r.CanRestore;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Rows)
        {
            r.IsSelected = false;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RestoreRow.IsSelected))
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(ShowArchiveNote));
            OnPropertyChanged(nameof(ShowRamWarning));
            OnPropertyChanged(nameof(RamWarningText));
        }
    }
}
