using System.IO.Compression;
using CalcLink.Core;
using Xunit;

namespace CalcLink.Core.Tests;

public sealed class LaunchArgumentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CalcLinkTests", Guid.NewGuid().ToString("N"));
    private readonly string _program;
    private readonly string _app;
    private readonly string _tig;

    public LaunchArgumentsTests()
    {
        Directory.CreateDirectory(_dir);
        _program = Path.Combine(_dir, "HELLO WORLD.8xp");
        File.WriteAllBytes(_program, TiVarFile.Write([new TiVarEntry("HELLO", 0x05, false, [1, 0, 0xE1])]));
        _app = Path.Combine(_dir, "Game.8xk");
        File.WriteAllBytes(_app, [.. "**TIFL**"u8.ToArray(), .. new byte[60]]);
        _tig = Path.Combine(_dir, "backup.tig");
        using var zip = ZipFile.Open(_tig, ZipArchiveMode.Create);
        zip.CreateEntry("x.8xp");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_plain_open_of_one_readable_file_shows_it()
    {
        var a = LaunchArguments.Parse([_program]);
        Assert.False(a.Send);
        Assert.Equal(_program, a.FileToOpen);
        Assert.Equal(_tig, LaunchArguments.Parse([_tig]).FileToOpen);
    }

    [Fact]
    public void Apps_several_files_and_send_go_to_the_send_queue()
    {
        Assert.Null(LaunchArguments.Parse([_app]).FileToOpen);                 // Files can't read apps
        Assert.Null(LaunchArguments.Parse([_program, _tig]).FileToOpen);       // several files
        var send = LaunchArguments.Parse(["--send", _program]);
        Assert.True(send.Send);
        Assert.Null(send.FileToOpen);
        Assert.Equal([_program], send.Files);
    }

    [Fact]
    public void Junk_switches_duplicates_and_the_exe_are_ignored()
    {
        string exe = Path.Combine(_dir, "CalcLink.exe");
        File.WriteAllText(exe, "");
        var a = LaunchArguments.Parse([exe, "-verbose", _program, _program.ToUpperInvariant(), Path.Combine(_dir, "missing.8xp")]);
        Assert.Equal([_program], a.Files);
    }

    [Fact]
    public void Parse_a_whole_command_line_with_quoted_paths()
    {
        var a = LaunchArguments.Parse($"\"C:\\Program Files\\CalcLink\\CalcLink.exe\" --send \"{_program}\"");
        Assert.True(a.Send);
        Assert.Equal([_program], a.Files);
    }

    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("\"a b\" c", new[] { "a b", "c" })]
    [InlineData("  ", new string[0])]
    [InlineData("\"\" x", new[] { "", "x" })]
    public void Split(string line, string[] expected) => Assert.Equal(expected, LaunchArguments.Split(line));

    [Fact]
    public void CanOpen_checks_the_header_not_just_the_extension()
    {
        string fake = Path.Combine(_dir, "fake.8xp");
        File.WriteAllText(fake, new string('x', 100));
        Assert.True(GroupFile.CanOpen(_program));
        Assert.False(GroupFile.CanOpen(fake));
        Assert.False(GroupFile.CanOpen(_app));
    }
}
