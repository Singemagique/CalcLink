namespace CalcLink.Core;

/// <summary>What a variable is, for grouping and filtering. Order = display order of groups.</summary>
public enum VariableCategory
{
    Programs,
    Apps,
    Documents,
    Numbers,
    Lists,
    Matrices,
    Equations,
    Strings,
    Pictures,
    Settings,
    Other,
}

public enum VariableSortKey { Name, Type, Size, Location }

public enum VariableGrouping { Folder, Category }

/// <summary>How to present the variable list.</summary>
/// <param name="SortKey">Primary sort key. Ties are always broken by name, ascending.</param>
/// <param name="Descending">Reverses the primary sort key.</param>
/// <param name="Grouping">Group by calculator folder, or by <see cref="VariableCategory"/>.</param>
/// <param name="Show">Only this category; null shows everything.</param>
/// <param name="Search">Case-insensitive substring of the name (or folder\name); null/empty = no filter.</param>
public sealed record VariableListOptions(
    VariableSortKey SortKey = VariableSortKey.Name,
    bool Descending = false,
    VariableGrouping Grouping = VariableGrouping.Folder,
    VariableCategory? Show = null,
    string? Search = null);

/// <summary>One group of the presented list.</summary>
/// <param name="Key">Folder name ("" = root) or category name.</param>
/// <param name="Title">Display title, e.g. "Calculator", "MAIN" or "Programs".</param>
/// <param name="Items">The group's variables, already filtered and sorted.</param>
public sealed record VariableListGroup(string Key, string Title, IReadOnlyList<CalcVariable> Items);

/// <summary>Sorting, grouping and filtering of a calculator's variable list (UI-independent).</summary>
public static class VariableList
{
    /// <summary>Title of the group holding variables that aren't in a folder.</summary>
    public const string RootTitle = "Calculator";

    // tilibs short type names (tifiles_vartype2string), across Z80, 68k and Nspire models.
    private static readonly Dictionary<string, VariableCategory> s_byTypeName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PRGM"] = VariableCategory.Programs, ["PPRGM"] = VariableCategory.Programs,
        ["ASM"] = VariableCategory.Programs, ["FUNC"] = VariableCategory.Programs,
        ["MAC"] = VariableCategory.Programs, ["PROG"] = VariableCategory.Programs,
        ["APPV"] = VariableCategory.Apps, ["APPL"] = VariableCategory.Apps, ["FLASH"] = VariableCategory.Apps,
        ["TNS"] = VariableCategory.Documents, ["TEXT"] = VariableCategory.Documents,
        ["REAL"] = VariableCategory.Numbers, ["CPLX"] = VariableCategory.Numbers, ["EXPR"] = VariableCategory.Numbers,
        ["LIST"] = VariableCategory.Lists, ["CLIST"] = VariableCategory.Lists, ["DATA"] = VariableCategory.Lists,
        ["MAT"] = VariableCategory.Matrices, ["MATRX"] = VariableCategory.Matrices,
        ["EQU"] = VariableCategory.Equations,
        ["STR"] = VariableCategory.Strings, ["STRNG"] = VariableCategory.Strings,
        ["PIC"] = VariableCategory.Pictures, ["IMAGE"] = VariableCategory.Pictures, ["FIG"] = VariableCategory.Pictures,
        ["WINDW"] = VariableCategory.Settings, ["ZSTO"] = VariableCategory.Settings, ["TABLE"] = VariableCategory.Settings,
        ["GDB"] = VariableCategory.Settings, ["LCD"] = VariableCategory.Settings,
    };

    public static VariableCategory CategoryOf(CalcVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (variable.IsApp)
        {
            return VariableCategory.Apps;
        }
        return s_byTypeName.TryGetValue(variable.TypeName.Trim(), out var category) ? category : VariableCategory.Other;
    }

    public static string CategoryTitle(VariableCategory category) => category switch
    {
        VariableCategory.Programs => "Programs",
        VariableCategory.Apps => "Apps & AppVars",
        VariableCategory.Documents => "Documents",
        VariableCategory.Numbers => "Numbers",
        VariableCategory.Lists => "Lists",
        VariableCategory.Matrices => "Matrices",
        VariableCategory.Equations => "Equations (Y=)",
        VariableCategory.Strings => "Strings",
        VariableCategory.Pictures => "Pictures",
        VariableCategory.Settings => "Window & graph settings",
        _ => "Other",
    };

    /// <summary>Filters, groups and sorts <paramref name="variables"/> according to <paramref name="options"/>.</summary>
    public static IReadOnlyList<VariableListGroup> Build(IEnumerable<CalcVariable> variables, VariableListOptions options)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(options);

        IEnumerable<CalcVariable> items = variables;
        if (options.Show is { } show)
        {
            items = items.Where(v => CategoryOf(v) == show);
        }
        if (!string.IsNullOrWhiteSpace(options.Search))
        {
            string term = options.Search.Trim();
            items = items.Where(v => v.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                     || v.FullName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return options.Grouping == VariableGrouping.Category
            ? items.GroupBy(CategoryOf)
                   .OrderBy(g => g.Key)
                   .Select(g => new VariableListGroup(g.Key.ToString(), CategoryTitle(g.Key), Sort(g, options)))
                   .ToList()
            : items.GroupBy(v => v.Folder ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                   .OrderBy(g => g.Key.Length == 0 ? 0 : 1)
                   .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                   .Select(g => new VariableListGroup(g.Key, g.Key.Length == 0 ? RootTitle : g.Key, Sort(g, options)))
                   .ToList();
    }

    private static IReadOnlyList<CalcVariable> Sort(IEnumerable<CalcVariable> items, VariableListOptions options)
    {
        IOrderedEnumerable<CalcVariable> ordered = options.SortKey switch
        {
            VariableSortKey.Type => Order(items, v => (int)CategoryOf(v), options.Descending)
                .ThenBy(v => v.TypeName, StringComparer.OrdinalIgnoreCase),
            VariableSortKey.Size => Order(items, v => v.Size, options.Descending),
            // RAM first (ascending), then archived/locked.
            VariableSortKey.Location => Order(items, v => v.Attribute == VariableAttribute.None ? 0 : 1, options.Descending),
            _ => options.Descending
                ? items.OrderByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IOrderedEnumerable<CalcVariable> Order<TKey>(IEnumerable<CalcVariable> items, Func<CalcVariable, TKey> key, bool descending)
        => descending ? items.OrderByDescending(key) : items.OrderBy(key);
}
