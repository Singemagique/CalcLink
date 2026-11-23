using System.IO.Compression;
using CalcLink.Core;
using Xunit;

namespace CalcLink.Core.Tests;

public sealed class GroupFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CalcLinkTests", Guid.NewGuid().ToString("N"));

    public GroupFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static TiVarEntry Program(string name, string source, bool archived = false)
    {
        byte[] tokens = TiBasic.Tokenize(source);
        return new TiVarEntry(name, 0x05, archived, [.. BitConverter.GetBytes((ushort)tokens.Length), .. tokens]);
    }

    private static TiVarEntry Raw(byte type, byte[] rawName, byte[] data) =>
        new("?", type, false, data) { RawName = rawName };

    [Fact]
    public void Write_then_Read_round_trips_a_group_with_token_names()
    {
        var group = new[]
        {
            Program("HELLO", "Disp \"HI\"", archived: true),
            Raw(0x01, [0x5D, 0x00, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0x80, 0x10, 0, 0, 0, 0, 0, 0]), // L₁ = {1}
            Raw(0x01, [0x5D, 0x41, 0x42, 0, 0, 0, 0, 0], [0, 0]),                            // ʟAB
            Raw(0x02, [0x5C, 0x01, 0, 0, 0, 0, 0, 0], [0, 0]),                               // [B]
            Raw(0x04, [0xAA, 0x00, 0, 0, 0, 0, 0, 0], [0, 0]),                               // Str1
            Raw(0x00, [0x5B, 0, 0, 0, 0, 0, 0, 0], new byte[9]),                             // θ
        };

        byte[] file = TiVarFile.Write(group);
        var back = TiVarFile.Read(file);

        Assert.Equal(["HELLO", "L₁", "ʟAB", "[B]", "Str1", "θ"], back.Select(e => e.Name));
        Assert.True(back[0].Archived);
        Assert.Equal(group[0].Data, back[0].Data);
        Assert.Equal("Disp \"HI\"", TiBasic.Detokenize(back[0].ProgramTokens!));
        Assert.Equal(file, TiVarFile.Write(back)); // byte-identical, checksum included
    }

    [Fact]
    public void Open_a_group_file_writes_each_item_to_its_own_file()
    {
        string path = Path.Combine(_dir, "MATHS.8xg");
        File.WriteAllBytes(path, TiVarFile.Write([Program("AREA", "Prompt R\nDisp πR²"), Program("VOL", "Prompt R,H")]));

        var contents = GroupFile.Open(path, Path.Combine(_dir, "work"));

        Assert.Equal(2, contents.Items.Count);
        var area = contents.Items[0];
        Assert.Equal("AREA", area.Name);
        Assert.Equal("PRGM", area.TypeCode);
        Assert.True(area.IsProgram);
        Assert.EndsWith("AREA.8xp", area.FilePath);
        var single = Assert.Single(TiVarFile.Read(area.FilePath));
        Assert.Equal("Prompt R\nDisp πR²", TiBasic.Detokenize(single.ProgramTokens!));
    }

    [Fact]
    public void Open_a_tig_lists_variables_apps_and_other_files()
    {
        string tig = Path.Combine(_dir, "backup.tig");
        using (var zip = ZipFile.Open(tig, ZipArchiveMode.Create))
        {
            Add(zip, "NOTES.8xp", TiVarFile.Write([Program("NOTES", "Disp \"A\"", archived: true)]));
            Add(zip, "MirageOS.8xk", [.. "**TIFL**"u8.ToArray(), .. new byte[40]]);
            Add(zip, "main/x.89p", [.. "**TI89**"u8.ToArray(), .. new byte[10]]);
        }

        var contents = GroupFile.Open(tig, Path.Combine(_dir, "work"));

        Assert.Equal(3, contents.Items.Count);
        var notes = contents.Items.Single(i => i.Name == "NOTES");
        Assert.True(notes.Archived);
        Assert.True(notes.IsProgram);
        var app = contents.Items.Single(i => i.IsApp);
        Assert.Equal("MirageOS", app.Name);
        Assert.Equal("APPL", app.TypeCode);
        var other = contents.Items.Single(i => i.Name == "x");
        Assert.Null(other.Entry);
        Assert.Equal("89P", other.TypeCode);
        Assert.All(contents.Items, i => Assert.True(File.Exists(i.FilePath)));
    }

    [Fact]
    public void Open_rejects_other_files()
    {
        string path = Path.Combine(_dir, "notes.8xg");
        File.WriteAllText(path, "not a calculator file at all, but long enough to have a header .....");
        Assert.Throws<FormatException>(() => GroupFile.Open(path, Path.Combine(_dir, "work")));
    }

    [Theory]
    [InlineData(0x05, "PRGM", ".8xp")]
    [InlineData(0x06, "PPRGM", ".8xp")]
    [InlineData(0x01, "LIST", ".8xl")]
    [InlineData(0x15, "APPV", ".8xv")]
    public void Type_codes_and_extensions(byte type, string code, string ext)
    {
        Assert.Equal(code, TiVarFile.TypeCodeOf(type));
        Assert.Equal(ext, TiVarFile.ExtensionOf(type));
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(data);
    }
}
