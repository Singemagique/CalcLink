using CalcLink.Core;

namespace CalcLink.Core.Tests;

public class VariableListTests
{
    private static CalcVariable Var(string name, string type, uint size, VariableAttribute attr = VariableAttribute.None,
        bool isApp = false, string folder = "") => new()
    {
        Folder = folder,
        Name = name,
        TypeName = type,
        FileExtension = "8xp",
        Size = size,
        RawType = 0,
        Attribute = attr,
        IsApp = isApp,
    };

    // A cut-down copy of the real TI-83 Plus list read over SilverLink.
    private static readonly CalcVariable[] Ti83p =
    [
        Var("Window", "WINDW", 0),
        Var("Y1", "EQU", 4),
        Var("A", "REAL", 9),
        Var("ACT", "PPRGM", 14756, VariableAttribute.Archived),
        Var("CIRCLE", "PRGM", 31, VariableAttribute.Archived),
        Var("TAYLOR", "PRGM", 366, VariableAttribute.Archived),
        Var("BOWLING", "PPRGM", 4960, VariableAttribute.Archived),
        Var("NOTES", "PRGM", 514),
        Var("L1", "LIST", 2),
        Var("MirageOS", "APPV", 27, VariableAttribute.Archived),
        Var("CBL/CBR", "APPL", 16384, isApp: true),
    ];

    private static string[] Names(IReadOnlyList<VariableListGroup> groups, int group = 0)
        => groups[group].Items.Select(v => v.Name).ToArray();

    [Theory]
    [InlineData("PRGM", VariableCategory.Programs)]
    [InlineData("PPRGM", VariableCategory.Programs)]
    [InlineData("APPV", VariableCategory.Apps)]
    [InlineData("REAL", VariableCategory.Numbers)]
    [InlineData("LIST", VariableCategory.Lists)]
    [InlineData("MAT", VariableCategory.Matrices)]
    [InlineData("EQU", VariableCategory.Equations)]
    [InlineData("STR", VariableCategory.Strings)]
    [InlineData("PIC", VariableCategory.Pictures)]
    [InlineData("WINDW", VariableCategory.Settings)]
    [InlineData("TNS", VariableCategory.Documents)]
    [InlineData("???", VariableCategory.Other)]
    public void CategoryOf_maps_tilibs_type_names(string typeName, VariableCategory expected)
        => Assert.Equal(expected, VariableList.CategoryOf(Var("X", typeName, 1)));

    [Fact]
    public void Apps_are_apps_whatever_their_type_name()
        => Assert.Equal(VariableCategory.Apps, VariableList.CategoryOf(Var("Foo", "PRGM", 1, isApp: true)));

    [Fact]
    public void Default_is_one_root_group_sorted_by_name()
    {
        var groups = VariableList.Build(Ti83p, new VariableListOptions());
        Assert.Single(groups);
        Assert.Equal(VariableList.RootTitle, groups[0].Title);
        Assert.Equal(["A", "ACT", "BOWLING", "CBL/CBR", "CIRCLE", "L1", "MirageOS", "NOTES", "TAYLOR", "Window", "Y1"],
            Names(groups));
    }

    [Fact]
    public void Descending_name_sort_reverses()
    {
        var groups = VariableList.Build(Ti83p, new VariableListOptions(Descending: true));
        Assert.Equal("Y1", Names(groups)[0]);
        Assert.Equal("A", Names(groups)[^1]);
    }

    [Fact]
    public void Size_sort_breaks_ties_by_name_ascending_even_when_descending()
    {
        CalcVariable[] vars = [Var("C", "REAL", 9), Var("A", "REAL", 9), Var("B", "REAL", 9), Var("Z", "PRGM", 50)];
        Assert.Equal(["A", "B", "C", "Z"], Names(VariableList.Build(vars, new VariableListOptions(VariableSortKey.Size))));
        Assert.Equal(["Z", "A", "B", "C"],
            Names(VariableList.Build(vars, new VariableListOptions(VariableSortKey.Size, Descending: true))));
    }

    [Fact]
    public void Size_sort_ascending_ends_with_largest()
    {
        var names = Names(VariableList.Build(Ti83p, new VariableListOptions(VariableSortKey.Size)));
        Assert.Equal("CBL/CBR", names[^1]);
        Assert.Equal("ACT", names[^2]);
    }

    [Fact]
    public void Location_sort_puts_ram_first()
    {
        var names = Names(VariableList.Build(Ti83p, new VariableListOptions(VariableSortKey.Location)));
        Assert.Equal(["A", "CBL/CBR", "L1", "NOTES", "Window", "Y1"], names[..6]);
        Assert.Equal(["ACT", "BOWLING", "CIRCLE", "MirageOS", "TAYLOR"], names[6..]);
    }

    [Fact]
    public void Category_grouping_puts_programs_first()
    {
        var groups = VariableList.Build(Ti83p, new VariableListOptions(Grouping: VariableGrouping.Category));
        Assert.Equal(["Programs", "Apps & AppVars", "Numbers", "Lists", "Equations (Y=)", "Window & graph settings"],
            groups.Select(g => g.Title).ToArray());
        Assert.Equal(["ACT", "BOWLING", "CIRCLE", "NOTES", "TAYLOR"], Names(groups, 0));
        Assert.Equal(["CBL/CBR", "MirageOS"], Names(groups, 1));
    }

    [Fact]
    public void Show_filters_to_one_category()
    {
        var groups = VariableList.Build(Ti83p, new VariableListOptions(Show: VariableCategory.Programs));
        Assert.Equal(["ACT", "BOWLING", "CIRCLE", "NOTES", "TAYLOR"], Names(groups));
    }

    [Fact]
    public void Programs_sorted_by_size_descending()
    {
        var groups = VariableList.Build(Ti83p,
            new VariableListOptions(VariableSortKey.Size, Descending: true, Show: VariableCategory.Programs));
        Assert.Equal(["ACT", "BOWLING", "NOTES", "TAYLOR", "CIRCLE"], Names(groups));
    }

    [Fact]
    public void Search_is_case_insensitive_substring()
    {
        var groups = VariableList.Build(Ti83p, new VariableListOptions(Search: "ow"));
        Assert.Equal(["BOWLING", "Window"], Names(groups));
    }

    [Fact]
    public void Empty_result_has_no_groups()
        => Assert.Empty(VariableList.Build(Ti83p, new VariableListOptions(Search: "zzz")));

    [Fact]
    public void Folder_grouping_puts_root_first_then_folders_alphabetically()
    {
        CalcVariable[] vars = [Var("b", "PRGM", 1, folder: "zeta"), Var("a", "PRGM", 1, folder: "main"), Var("x", "REAL", 1)];
        var groups = VariableList.Build(vars, new VariableListOptions());
        Assert.Equal([VariableList.RootTitle, "main", "zeta"], groups.Select(g => g.Title).ToArray());
    }
}
