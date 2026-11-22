namespace CalcLink.Core.Tests;

public class FileNameTests
{
    [Theory]
    [InlineData("QUADFORM", "QUADFORM")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("COM10", "COM10")]
    [InlineData("trailing. . ", "trailing")]
    [InlineData("  lead", "lead")]
    [InlineData("", "_")]
    [InlineData("...", "_")]
    [InlineData("L₁", "L₁")]
    [InlineData("[A]", "[A]")]
    [InlineData("θ", "θ")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, CalcFileNames.Sanitize(input));

    [Theory]
    [InlineData("QUADFORM", "8xp", "QUADFORM.8xp")]
    [InlineData("AUX", "8xv", "_AUX.8xv")]
    [InlineData("snake.py", "tns", "snake.py.tns")]
    [InlineData("Str?", ".8xs", "Str_.8xs")]
    [InlineData("noext", "", "noext")]
    public void ForVariable(string name, string ext, string expected) => Assert.Equal(expected, CalcFileNames.ForVariable(name, ext));

    [Fact]
    public void MakeUnique_DeduplicatesAgainstBatchAndDisk()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "A.8xn"), "x");
        var taken = CalcFileNames.NewPathSet();

        string first = CalcFileNames.MakeUnique(dir.Path, "A.8xn", taken);
        string second = CalcFileNames.MakeUnique(dir.Path, "a.8xn", taken);
        string other = CalcFileNames.MakeUnique(dir.Path, "B.8xn", taken);

        Assert.Equal(Path.Combine(dir.Path, "A (2).8xn"), first);
        Assert.Equal(Path.Combine(dir.Path, "a (3).8xn"), second);
        Assert.Equal(Path.Combine(dir.Path, "B.8xn"), other);
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CalcLink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
