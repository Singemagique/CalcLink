using System.Buffers.Binary;
using System.Text;

namespace CalcLink.Core;

/// <summary>One variable inside a TI-83 / TI-83 Plus / TI-84 Plus variable file.</summary>
/// <param name="Name">Name as the calculator shows it (θ for token 5B).</param>
/// <param name="TypeId">Variable type (05 = program, 06 = protected program, ...).</param>
/// <param name="Archived">The file records it as archived.</param>
/// <param name="Data">The variable's data, as stored on the calculator.</param>
public sealed record TiVarEntry(string Name, byte TypeId, bool Archived, byte[] Data)
{
    public bool IsProgram => TypeId is 0x05 or 0x06;

    /// <summary>A program's tokens: its data minus the 2-byte length in front. Null if not a program.</summary>
    public byte[]? ProgramTokens
    {
        get
        {
            if (!IsProgram || Data.Length < 2)
            {
                return null;
            }
            int length = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(Data), Data.Length - 2);
            return Data.AsSpan(2, length).ToArray();
        }
    }
}

/// <summary>
/// Reads TI-83 ("**TI83**") and TI-83 Plus / TI-84 Plus family ("**TI83F*") variable files such as
/// .8xp, .83p and .8xv. These all share one layout: a 55-byte header, then entries of
/// [header length, data length, type, 8-byte name, (version, flags,) data length, data].
/// </summary>
public static class TiVarFile
{
    private const int HeaderSize = 55;

    /// <summary>True when <paramref name="bytes"/> look like a TI-83 or TI-83 Plus family file.</summary>
    public static bool IsSupported(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= HeaderSize && (bytes.StartsWith("**TI83F*"u8) || bytes.StartsWith("**TI83**"u8));

    public static IReadOnlyList<TiVarEntry> Read(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Parses the entries. Throws <see cref="FormatException"/> for other or damaged files.</summary>
    public static IReadOnlyList<TiVarEntry> Read(ReadOnlySpan<byte> bytes)
    {
        if (!IsSupported(bytes))
        {
            throw new FormatException("This isn't a TI-83 or TI-83 Plus / TI-84 Plus variable file.");
        }
        int dataLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[53..]);
        int end = Math.Min(bytes.Length, HeaderSize + dataLength);
        var entries = new List<TiVarEntry>();
        int pos = HeaderSize;
        while (pos + 4 <= end)
        {
            int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[pos..]);
            int varLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(pos + 2)..]);
            if (headerLength is not (0x0B or 0x0D) || pos + 2 + headerLength + 2 + varLength > end)
            {
                throw new FormatException("The variable file is damaged.");
            }
            byte type = bytes[pos + 4];
            string name = DecodeName(bytes.Slice(pos + 5, 8));
            bool archived = headerLength == 0x0D && (bytes[pos + 14] & 0x80) != 0;
            int dataStart = pos + 2 + headerLength + 2;
            entries.Add(new TiVarEntry(name, type, archived, bytes.Slice(dataStart, varLength).ToArray()));
            pos = dataStart + varLength;
        }
        return entries;
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var sb = new StringBuilder(8);
        foreach (byte b in raw)
        {
            if (b == 0)
            {
                break;
            }
            sb.Append(b == 0x5B ? 'θ' : b is >= 0x20 and < 0x7F ? (char)b : '?');
        }
        return sb.ToString();
    }
}

/// <summary>What the program viewer shows for one program.</summary>
public enum ProgramKind { Basic, Assembly }

/// <summary>A program prepared for display.</summary>
/// <param name="Name">Program name.</param>
/// <param name="Kind">TI-BASIC or assembly.</param>
/// <param name="ByteCount">Size of the program's tokens or machine code.</param>
/// <param name="LineCount">Lines in <paramref name="Text"/>.</param>
/// <param name="Text">BASIC: the listing ('\n' between lines). Assembly: a hex dump.</param>
public sealed record ProgramListing(string Name, ProgramKind Kind, int ByteCount, int LineCount, string Text)
{
    /// <summary>Builds the listing for a program's tokens.</summary>
    public static ProgramListing From(string name, ReadOnlySpan<byte> tokens)
    {
        if (TiBasic.IsAssembly(tokens))
        {
            string hex = HexDump(tokens);
            return new ProgramListing(name, ProgramKind.Assembly, tokens.Length, hex.Count(c => c == '\n') + 1, hex);
        }
        string text = TiBasic.Detokenize(tokens);
        return new ProgramListing(name, ProgramKind.Basic, tokens.Length, text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1, text);
    }

    /// <summary>
    /// 16 bytes per line: offset, hex bytes, then the printable ASCII characters (others as '.'), so
    /// text inside a program, such as a game's title, is readable.
    /// </summary>
    public static string HexDump(ReadOnlySpan<byte> bytes)
    {
        const int HexWidth = 16 * 3; // a full line's hex part: 16 "XX", 15 single gaps, one double gap
        var sb = new StringBuilder(bytes.Length * 4 + bytes.Length / 16 * 10);
        for (int i = 0; i < bytes.Length; i += 16)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }
            sb.Append(i.ToString("X4")).Append("  ");
            int n = Math.Min(16, bytes.Length - i);
            int start = sb.Length;
            for (int k = 0; k < n; k++)
            {
                sb.Append(bytes[i + k].ToString("X2"));
                if (k < n - 1)
                {
                    sb.Append(k == 7 ? "  " : " ");
                }
            }
            sb.Append(' ', HexWidth - (sb.Length - start) + 2); // two spaces before the text column
            for (int k = 0; k < n; k++)
            {
                byte b = bytes[i + k];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
        }
        return sb.ToString();
    }
}
