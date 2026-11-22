using System.IO.Compression;
using CalcLink.Core;
using Xunit;

namespace CalcLink.Core.Tests;

public sealed class BackupsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CalcLinkTests", Guid.NewGuid().ToString("N"));

    public BackupsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("TI-83 Plus", "040123AB4CD5E6", "TI-83 Plus (D5E6)")]
    [InlineData("TI-83 Plus", "", "TI-83 Plus")]
    [InlineData("TI-83 Plus", null, "TI-83 Plus")]
    [InlineData("Bad:Name?", "12", "Bad_Name_")]
    public void CalculatorFolder_names_one_folder_per_calculator(string model, string? id, string expected) =>
        Assert.Equal(Path.Combine(@"C:\B", expected), Backups.CalculatorFolder(@"C:\B", model, id));

    [Fact]
    public void NewBackupPath_is_dated_and_never_reuses_a_name()
    {
        var when = new DateTime(2026, 11, 22, 14, 19, 5);
        string first = Backups.NewBackupPath(_dir, when, "tig");
        Assert.Equal(Path.Combine(_dir, "2026-11-22 14-19-05.tig"), first);
        File.WriteAllText(first, "x");
        Assert.Equal(Path.Combine(_dir, "2026-11-22 14-19-05 (2).tig"), Backups.NewBackupPath(_dir, when, ".tig"));
    }

    [Fact]
    public void List_returns_backups_newest_first_with_item_counts_and_skips_partials()
    {
        MakeTig(Path.Combine(_dir, "2026-11-20 09-00-00.tig"), "A.8xp", "B.8xp");
        MakeTig(Path.Combine(_dir, "2026-11-22 14-19-05.tig"), "A.8xp");
        File.WriteAllText(Path.Combine(_dir, "2026-11-21 10-00-00.8xb"), "ram");
        File.WriteAllText(Path.Combine(_dir, "2026-11-23 10-00-00.abc.partial.tig"), "in progress");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a backup");

        var list = Backups.List(_dir, [".tig", "8xb"]);

        Assert.Equal(3, list.Count);
        Assert.Equal(new DateTime(2026, 11, 22, 14, 19, 5), list[0].Created);
        Assert.Equal(1, list[0].ItemCount);
        Assert.Null(list[1].ItemCount); // the .8xb RAM image
        Assert.False(list[1].IsTiGroup);
        Assert.Equal(2, list[2].ItemCount);
    }

    [Fact]
    public void List_of_a_missing_folder_is_empty() =>
        Assert.Empty(Backups.List(Path.Combine(_dir, "nope"), [".tig"]));

    [Fact]
    public void ExtractTiGroup_flattens_entries_and_keeps_them_inside_the_folder()
    {
        string tig = Path.Combine(_dir, "b.tig");
        MakeTig(tig, "A.8xp", "main/B.89p", "../../evil.8xp", "a.8xp");
        string dest = Path.Combine(_dir, "out");

        var files = Backups.ExtractTiGroup(tig, dest);

        Assert.Equal(4, files.Count);
        Assert.All(files, f => Assert.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar, f));
        Assert.Contains(files, f => Path.GetFileName(f) == "evil.8xp");
        Assert.Contains(files, f => Path.GetFileName(f) == "a (2).8xp"); // same name, different case
        Assert.False(File.Exists(Path.Combine(_dir, "evil.8xp")));
    }

    [Theory]
    [InlineData(CalcModel.TI83P, true)]
    [InlineData(CalcModel.TI84PCE_USB, true)]
    [InlineData(CalcModel.TI83, false)]
    [InlineData(CalcModel.TI89, false)]
    public void UsesTiGroup_on_flash_models(CalcModel model, bool expected) =>
        Assert.Equal(expected, Backups.UsesTiGroup(model));

    private static void MakeTig(string path, params string[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string e in entries)
        {
            using var s = zip.CreateEntry(e).Open();
            s.Write("data"u8);
        }
    }
}

public class RestorePlanTests
{
    private static CalcVariable Var(string name, string type, uint size, VariableAttribute attr = VariableAttribute.None,
        bool app = false, byte raw = 0x05) => new()
    {
        Folder = "", Name = name, TypeName = type, FileExtension = "8xp", Size = size, RawType = raw,
        Attribute = attr, IsApp = app,
    };

    [Fact]
    public void Build_marks_missing_present_and_installed_items()
    {
        var onCalc = new[] { Var("ACT", "PRGM", 100), Var("CBL/CBR", "APPL", 0, app: true, raw: 0x24) };
        var vars = new (string, IReadOnlyList<CalcVariable>)[]
        {
            ("a.8xp", [Var("ACT", "PRGM", 120, VariableAttribute.Archived)]),
            ("b.8xp", [Var("GONE", "PRGM", 50, VariableAttribute.Archived)]),
        };
        var apps = new (string, string, long)[] { ("c.8xk", "CBL_CBR", 33370), ("d.8xk", "MirageOS", 39530) };

        var plan = RestorePlan.Build(CalcModel.TI83P, onCalc, vars, apps);

        Assert.Equal(RestoreStatus.OnCalculator, plan.Single(i => i.Name == "ACT").Status);
        var gone = plan.Single(i => i.Name == "GONE");
        Assert.Equal(RestoreStatus.Missing, gone.Status);
        Assert.True(gone.WasArchived);
        Assert.True(gone.SelectedByDefault);
        Assert.Equal(RestoreStatus.Installed, plan.Single(i => i.Name == "CBL_CBR").Status); // "/" saved as "_"
        Assert.False(plan.Single(i => i.Name == "CBL_CBR").CanRestore);
        Assert.Equal(RestoreStatus.Missing, plan.Single(i => i.Name == "MirageOS").Status);
    }

    [Fact]
    public void RamNeeded_counts_variables_only() =>
        Assert.Equal(170, RestorePlan.RamNeeded(
        [
            new RestoreItem("a", "A", Var("A", "PRGM", 120), 120, false, RestoreStatus.Missing),
            new RestoreItem("b", "B", Var("B", "PRGM", 50), 50, true, RestoreStatus.Missing),
            new RestoreItem("c", "APP", null, 30000, false, RestoreStatus.Missing),
        ]));
}
