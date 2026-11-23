using System.Buffers.Binary;
using CalcLink.Core;
using Xunit;

namespace CalcLink.Core.Tests;

public class TiBasicTests
{
    [Fact]
    public void Detokenize_reads_common_program_tokens()
    {
        // :ClrHome / :Input "N=",N / :For(I,1,N) / :Disp I² / :End
        byte[] tokens =
        [
            0xE1, 0x3F,
            0xDC, 0x2A, 0x4E, 0x6A, 0x2A, 0x2B, 0x4E, 0x3F,
            0xD3, 0x49, 0x2B, 0x31, 0x2B, 0x4E, 0x11, 0x3F,
            0xDE, 0x49, 0x0D, 0x3F,
            0xD4,
        ];
        Assert.Equal("ClrHome\nInput \"N=\",N\nFor(I,1,N)\nDisp I²\nEnd", TiBasic.Detokenize(tokens));
    }

    [Theory]
    [InlineData(new byte[] { 0x5D, 0x00 }, "L₁")]
    [InlineData(new byte[] { 0x5C, 0x02 }, "[C]")]
    [InlineData(new byte[] { 0x5E, 0x10 }, "Y₁")]
    [InlineData(new byte[] { 0xAA, 0x09 }, "Str0")]
    [InlineData(new byte[] { 0x63, 0x0A }, "Xmin")]
    [InlineData(new byte[] { 0x7E, 0x09 }, "AxesOff")]
    [InlineData(new byte[] { 0xBB, 0x0A }, "randInt(")]
    [InlineData(new byte[] { 0xBB, 0x54 }, "DelVar ")]
    [InlineData(new byte[] { 0xBB, 0xB0, 0xBB, 0xBC, 0xBB, 0xCA }, "alz")]
    [InlineData(new byte[] { 0xBB, 0x7A }, "é")]
    [InlineData(new byte[] { 0x04, 0xC1, 0xBF }, "→₁₀^(ℯ^(")]
    [InlineData(new byte[] { 0x5F, 0x41, 0x42 }, "prgmAB")]
    public void Detokenize_two_byte_and_special_tokens(byte[] tokens, string expected) =>
        Assert.Equal(expected, TiBasic.Detokenize(tokens));

    [Theory]
    [InlineData(new byte[] { 0xBB, 0x7E }, "[BB 7E]")] // unused in TI's table: shown, not guessed
    [InlineData(new byte[] { 0x26 }, "[26]")]
    [InlineData(new byte[] { 0x41, 0xBB }, "A[BB]")] // prefix cut off at the end
    public void Detokenize_marks_unknown_tokens(byte[] tokens, string expected) =>
        Assert.Equal(expected, TiBasic.Detokenize(tokens));

    [Theory]
    [InlineData("ClrHome\nDisp \"HELLO\"")]
    [InlineData("Prompt A,B,C\nB²-4AC→D\nIf D<0\nThen\nDisp \"NO REAL ROOTS\"\nElse\nDisp (⁻B+√(D))/(2A)\nEnd")]
    [InlineData("For(I,1,dim(L₁))\nL₁(I)²→L₂(I)\nEnd\nrandInt(1,6)→X\nStr1+\"abc\"→Str2")]
    [InlineData("Menu(\"MATHS\",\"AREA\",A,\"QUIT\",Q)\nLbl Q\nStop")]
    [InlineData("Xmin→A\nAxesOff\n[A]ᵀ→[B]\n[BB 7E]")]
    public void Tokenize_round_trips(string text) =>
        Assert.Equal(text, TiBasic.Detokenize(TiBasic.Tokenize(text)));

    [Fact]
    public void Tokenize_prefers_whole_commands_and_one_byte_tokens()
    {
        Assert.Equal([0xDE, 0x41], TiBasic.Tokenize("Disp A"));            // "Disp ", not D-i-s-p
        Assert.Equal([0xA6], TiBasic.Tokenize("Horizontal "));            // longest match beats "Horiz"
        Assert.Equal([0xAC], TiBasic.Tokenize("π"));                      // one-byte π over the Greek letter
        Assert.Equal([0xBB, 0xB0], TiBasic.Tokenize("a"));                // lowercase over the statistic a
    }

    [Fact]
    public void Tokenize_rejects_text_without_a_token() =>
        Assert.Throws<FormatException>(() => TiBasic.Tokenize("½"));

    [Theory]
    [InlineData(new byte[] { 0xBB, 0x6D, 0xC9 }, true)]   // TI-83 Plus compiled assembly
    [InlineData(new byte[] { 0xEF, 0x7B, 0xC9 }, true)]   // TI-84 Plus CE assembly
    [InlineData(new byte[] { 0xBB, 0x6C, 0x43, 0x39 }, false)] // AsmPrgm + hex text is readable
    [InlineData(new byte[] { 0xE1 }, false)]
    public void IsAssembly(byte[] tokens, bool expected) => Assert.Equal(expected, TiBasic.IsAssembly(tokens));

    [Fact]
    public void Listing_shows_assembly_as_a_hex_dump()
    {
        byte[] asm = [0xBB, 0x6D, .. Enumerable.Range(0, 18).Select(i => (byte)i)];
        var listing = ProgramListing.From("GAME", asm);
        Assert.Equal(ProgramKind.Assembly, listing.Kind);
        Assert.Equal(20, listing.ByteCount);
        string[] lines = listing.Text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("0000  BB 6D 00 01 02 03 04 05  06 07 08 09 0A 0B 0C 0D  .m..............", lines[0]);
        // The text column lines up on a short last line too.
        Assert.Equal("0010  0E 0F 10 11" + new string(' ', 39) + "....", lines[1]);
    }

    [Fact]
    public void HexDump_shows_text_inside_the_program() =>
        Assert.EndsWith("Phoenix", ProgramListing.HexDump("Phoenix"u8).TrimEnd());

    [Fact]
    public void TiVarFile_reads_a_program_file()
    {
        byte[] tokens = TiBasic.Tokenize("ClrHome\nDisp \"HI\"");
        byte[] file = MakeProgramFile("HELLO", 0x06, archived: true, tokens);

        var entry = Assert.Single(TiVarFile.Read(file));

        Assert.Equal("HELLO", entry.Name);
        Assert.True(entry.IsProgram);
        Assert.True(entry.Archived);
        Assert.Equal(tokens, entry.ProgramTokens);
        Assert.Equal("ClrHome\nDisp \"HI\"", ProgramListing.From(entry.Name, entry.ProgramTokens!).Text);
    }

    [Fact]
    public void TiVarFile_rejects_other_files() =>
        Assert.Throws<FormatException>(() => TiVarFile.Read("**TI89**not a TI-83 file at all, but long enough ..........."u8));

    // A minimal **TI83F* file with one entry (13-byte entry header).
    private static byte[] MakeProgramFile(string name, byte type, bool archived, byte[] tokens)
    {
        byte[] varData = [.. BitConverter.GetBytes((ushort)tokens.Length), .. tokens];
        var entry = new List<byte>();
        entry.AddRange(BitConverter.GetBytes((ushort)0x0D));
        entry.AddRange(BitConverter.GetBytes((ushort)varData.Length));
        entry.Add(type);
        byte[] nameBytes = new byte[8];
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
        entry.AddRange(nameBytes);
        entry.Add(0);
        entry.Add(archived ? (byte)0x80 : (byte)0);
        entry.AddRange(BitConverter.GetBytes((ushort)varData.Length));
        entry.AddRange(varData);

        var file = new List<byte>();
        file.AddRange("**TI83F*"u8.ToArray());
        file.AddRange(new byte[] { 0x1A, 0x0A, 0x00 });
        file.AddRange(new byte[42]);
        file.AddRange(BitConverter.GetBytes((ushort)entry.Count));
        file.AddRange(entry);
        ushort sum = 0;
        foreach (byte b in entry)
        {
            sum += b;
        }
        file.AddRange(BitConverter.GetBytes(sum));
        return [.. file];
    }
}
