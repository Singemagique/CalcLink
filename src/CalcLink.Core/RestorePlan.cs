namespace CalcLink.Core;

/// <summary>Whether an item from a backup is on the calculator now.</summary>
public enum RestoreStatus
{
    /// <summary>Not on the calculator: restoring adds it.</summary>
    Missing,
    /// <summary>A variable with the same identity is on the calculator: restoring replaces it.</summary>
    OnCalculator,
    /// <summary>An app that is already installed. Apps can't be overwritten, so it isn't restored.</summary>
    Installed,
}

/// <summary>One variable or app in a backup, ready to be offered for restore.</summary>
/// <param name="Path">The extracted file to send.</param>
/// <param name="Name">Display name.</param>
/// <param name="Variable">The variable as read from the file; null for an app.</param>
/// <param name="Size">Bytes on the calculator (variable size, or the app file's size).</param>
/// <param name="WasArchived">The variable was in the archive when the backup was made.</param>
/// <param name="Status">Whether it is on the calculator now.</param>
public sealed record RestoreItem(string Path, string Name, CalcVariable? Variable, long Size, bool WasArchived, RestoreStatus Status)
{
    public bool IsApp => Variable is null;

    /// <summary>Pre-selected in the restore dialog: only what is missing from the calculator.</summary>
    public bool SelectedByDefault => Status == RestoreStatus.Missing;

    public bool CanRestore => Status != RestoreStatus.Installed;
}

/// <summary>Compares a backup's contents with what is on the calculator.</summary>
public static class RestorePlan
{
    /// <param name="model">Connected model (decides how names are matched, see <see cref="VariableConflicts"/>).</param>
    /// <param name="onCalculator">The calculator's current list, including apps.</param>
    /// <param name="variables">Each extracted variable file with the variables read from it.</param>
    /// <param name="apps">Each extracted app file with its app name and size.</param>
    public static IReadOnlyList<RestoreItem> Build(CalcModel model, IReadOnlyList<CalcVariable> onCalculator,
        IEnumerable<(string Path, IReadOnlyList<CalcVariable> Variables)> variables,
        IEnumerable<(string Path, string Name, long Size)> apps)
    {
        var items = new List<RestoreItem>();
        foreach (var (path, vars) in variables)
        {
            var clashes = VariableConflicts.Find(model, onCalculator, [(path, vars)]);
            foreach (var v in vars)
            {
                bool there = clashes.Any(c => ReferenceEquals(c.Incoming, v));
                items.Add(new RestoreItem(path, v.Name, v, v.Size, v.Attribute == VariableAttribute.Archived,
                    there ? RestoreStatus.OnCalculator : RestoreStatus.Missing));
            }
        }
        var installed = onCalculator.Where(v => v.IsApp).Select(v => AppKey(v.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, name, size) in apps)
        {
            items.Add(new RestoreItem(path, name, null, size, WasArchived: false,
                installed.Contains(AppKey(name)) ? RestoreStatus.Installed : RestoreStatus.Missing));
        }
        return items;
    }

    // App file names replace characters Windows doesn't allow ("CBL/CBR" is saved as "CBL_CBR "), so
    // compare letters and digits only.
    private static string AppKey(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// Bytes of RAM the selection needs when the calculator can't receive straight into the archive:
    /// every selected variable lands in RAM (apps go to flash and don't count).
    /// </summary>
    public static long RamNeeded(IEnumerable<RestoreItem> selected) =>
        selected.Where(i => !i.IsApp).Sum(i => i.Size);
}
