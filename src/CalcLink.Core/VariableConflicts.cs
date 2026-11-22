namespace CalcLink.Core;

/// <summary>An incoming variable (from a file) that has the same identity as one already on the calculator.</summary>
public sealed record VariableConflict(string Path, CalcVariable Incoming, CalcVariable Existing);

/// <summary>Finds variables that sending files would overwrite (or that the calculator would refuse).</summary>
public static class VariableConflicts
{
    /// <summary>
    /// Matches each file's variables against the calculator's list.
    /// <list type="bullet">
    /// <item>TI-73/83/84 family: one name can hold one variable per kind, so a program <c>A</c> and a
    /// number <c>A</c> coexist. A conflict is the same name and the same <see cref="VariableCategory"/>
    /// (which also pairs program/protected program, real/complex and list/complex list).</item>
    /// <item>Other models (68k, Nspire): names are unique within a folder whatever the type. A file
    /// variable without a folder lands in the current folder, so it is matched against every folder.</item>
    /// </list>
    /// Names compare case-insensitively: a false alarm only costs a question, a miss costs data.
    /// </summary>
    public static IReadOnlyList<VariableConflict> Find(CalcModel model, IEnumerable<CalcVariable> existing,
        IEnumerable<(string Path, IReadOnlyList<CalcVariable> Variables)> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        CalcVariable[] onCalc = existing.Where(v => !v.IsApp).ToArray();
        bool perKind = CalcIdentity.IsZ80IdFamily(model) || model is CalcModel.TI82 or CalcModel.TI83
            or CalcModel.TI85 or CalcModel.TI86 or CalcModel.TI80;

        var result = new List<VariableConflict>();
        foreach (var (path, vars) in incoming)
        {
            foreach (CalcVariable v in vars)
            {
                CalcVariable? hit = onCalc.FirstOrDefault(e =>
                    string.Equals(e.Name, v.Name, StringComparison.OrdinalIgnoreCase)
                    && (v.Folder.Length == 0 || string.Equals(e.Folder, v.Folder, StringComparison.OrdinalIgnoreCase))
                    && (!perKind || VariableList.CategoryOf(e) == VariableList.CategoryOf(v)));
                if (hit is not null)
                {
                    result.Add(new VariableConflict(path, v, hit));
                }
            }
        }
        return result;
    }
}
