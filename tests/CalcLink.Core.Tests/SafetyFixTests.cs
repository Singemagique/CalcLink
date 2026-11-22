using CalcLink.Core.Native;
using CalcLink.Core.Simulation;

namespace CalcLink.Core.Tests;

/// <summary>Regression tests for the data-safety fixes (docs/REVIEW.md B1, B2, B9).</summary>
public sealed class SafetyFixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("calclink-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- B1: backup / ROM dump write through a temp file -------------------------------------------

    [Fact]
    public void Failed_write_keeps_the_existing_file_and_leaves_no_temp()
    {
        string path = Path.Combine(_dir, "backup.8xb");
        File.WriteAllText(path, "last month's backup");

        Assert.Throws<CalcLinkException>(() => NativeCalculatorSession.WriteViaTempFile(path,
            temp => { File.WriteAllText(temp, "partial"); return CalcErrorCodes.Cancelled; },
            rc => throw new CalcLinkException(rc, "failed")));

        Assert.Equal("last month's backup", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Successful_write_replaces_the_file_and_keeps_the_extension()
    {
        string path = Path.Combine(_dir, "class set.tig");
        File.WriteAllText(path, "old");
        string? used = null;

        NativeCalculatorSession.WriteViaTempFile(path,
            temp => { used = temp; File.WriteAllText(temp, "new"); return CalcErrorCodes.Ok; },
            _ => throw new InvalidOperationException("unexpected failure"));

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(".tig", Path.GetExtension(used)); // tilibs picks the backup format from it
        Assert.Equal(_dir, Path.GetDirectoryName(used)); // same volume: the move is a rename
        Assert.NotEqual(path, used);
    }

    [Fact]
    public void Write_that_fails_before_creating_anything_leaves_the_folder_unchanged()
    {
        string path = Path.Combine(_dir, "rom.rom");
        Assert.Throws<CalcLinkException>(() => NativeCalculatorSession.WriteViaTempFile(path,
            _ => CalcErrorCodes.NotSupported, rc => throw new CalcLinkException(rc, "failed")));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    // ---- B2: setting the clock keeps the display formats --------------------------------------------

    [Fact]
    public async Task Setting_the_clock_with_unspecified_formats_keeps_the_calculators_formats()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant);
        var device = (await svc.ProbeAsync()).Single(d => d.Model == CalcModel.TI84PCE_USB);
        await using var session = await svc.ConnectAsync(device);

        await session.SetClockAsync(new CalcClock(new DateTime(2026, 1, 2, 3, 4, 5), TimeFormat: 12, DateFormat: 2));
        await session.SetClockAsync(new CalcClock(new DateTime(2026, 6, 7, 8, 9, 10), TimeFormat: 0, DateFormat: 0));

        CalcClock after = await session.GetClockAsync();
        Assert.Equal(12, after.TimeFormat);
        Assert.Equal(2, after.DateFormat);
        Assert.Equal(new DateTime(2026, 6, 7), after.Value.Date);
    }

    // ---- B9: conflicts before sending --------------------------------------------------------------

    private static CalcVariable Var(string name, string type, string folder = "", bool isApp = false,
        VariableAttribute attr = VariableAttribute.None) => new()
    {
        Folder = folder, Name = name, TypeName = type, FileExtension = "8xp", Size = 10, RawType = 0,
        Attribute = attr, IsApp = isApp,
    };

    private static IReadOnlyList<VariableConflict> Find(CalcModel model, CalcVariable[] onCalc, params CalcVariable[] incoming)
        => VariableConflicts.Find(model, onCalc, [("file", incoming)]);

    [Fact]
    public void Same_program_name_conflicts_case_insensitively()
    {
        var hits = Find(CalcModel.TI83P, [Var("TAYLOR", "PRGM", attr: VariableAttribute.Archived)], Var("taylor", "PRGM"));
        Assert.Single(hits);
        Assert.Equal("TAYLOR", hits[0].Existing.Name);
    }

    [Fact]
    public void On_ti8x_a_program_and_a_number_with_the_same_name_coexist()
        => Assert.Empty(Find(CalcModel.TI84P, [Var("A", "REAL")], Var("A", "PRGM")));

    [Theory]
    [InlineData("PRGM", "PPRGM")] // protected and unprotected programs share names
    [InlineData("REAL", "CPLX")]
    [InlineData("LIST", "CLIST")]
    public void On_ti8x_related_types_share_a_name(string onCalc, string incoming)
        => Assert.Single(Find(CalcModel.TI83P, [Var("X", onCalc)], Var("X", incoming)));

    [Fact]
    public void On_68k_names_clash_within_a_folder_whatever_the_type()
    {
        CalcVariable[] onCalc = [Var("prog1", "EXPR", "main"), Var("prog2", "PRGM", "games")];
        Assert.Single(Find(CalcModel.TI89T, onCalc, Var("prog1", "PRGM", "main")));
        Assert.Empty(Find(CalcModel.TI89T, onCalc, Var("prog1", "PRGM", "other")));
        Assert.Single(Find(CalcModel.TI89T, onCalc, Var("prog2", "PRGM", ""))); // no folder: any folder
    }

    [Fact]
    public void Apps_on_the_calculator_are_ignored()
        => Assert.Empty(Find(CalcModel.TI84P, [Var("CABRI", "APPL", isApp: true)], Var("CABRI", "PRGM")));

    [Fact]
    public async Task File_variables_of_a_non_calculator_file_are_empty()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant);
        string path = Path.Combine(_dir, "readme.txt");
        File.WriteAllText(path, "not a calculator file");
        await using var session = await svc.ConnectAsync((await svc.ProbeAsync())[0]);
        Assert.Empty(session.GetFileVariables(path));
    }
}
