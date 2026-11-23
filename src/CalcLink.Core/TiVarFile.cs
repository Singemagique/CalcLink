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
    /// <summary>The 8-byte name as stored (tokens for lists, matrices, ...), kept so writing round-trips.</summary>
    public byte[]? RawName { get; init; }

    /// <summary>Version byte of the 13-byte entry header (0 when absent).</summary>
    public byte Version { get; init; }

    public bool IsProgram => TypeId is 0x05 or 0x06;

    /// <summary>tilibs-style type code ("PRGM", "REAL", ...), as the calculator's list shows it.</summary>
    public string TypeCode => TiVarFile.TypeCodeOf(TypeId);

    /// <summary>File extension (with dot) for a file holding just this variable, e.g. ".8xp".</summary>
    public string FileExtension => TiVarFile.ExtensionOf(TypeId);

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
            byte[] rawName = bytes.Slice(pos + 5, 8).ToArray();
            bool archived = headerLength == 0x0D && (bytes[pos + 14] & 0x80) != 0;
            byte version = headerLength == 0x0D ? bytes[pos + 13] : (byte)0;
            int dataStart = pos + 2 + headerLength + 2;
            entries.Add(new TiVarEntry(DecodeName(type, rawName), type, archived, bytes.Slice(dataStart, varLength).ToArray())
            {
                RawName = rawName,
                Version = version,
            });
            pos = dataStart + varLength;
        }
        return entries;
    }

    /// <summary>
    /// Writes a TI-83 Plus family file ("**TI83F*", 13-byte entry headers) holding <paramref name="entries"/>:
    /// one entry makes an ordinary variable file, several make a group (.8xg). The archived flag is
    /// kept, as TI Connect does.
    /// </summary>
    public static byte[] Write(IReadOnlyList<TiVarEntry> entries, string comment = "Created by CalcLink")
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var body = new MemoryStream();
        Span<byte> head = stackalloc byte[17];
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(head, 0x0D);
            BinaryPrimitives.WriteUInt16LittleEndian(head[2..], checked((ushort)e.Data.Length));
            head[4] = e.TypeId;
            head.Slice(5, 8).Clear();
            byte[] name = e.RawName ?? Encoding.ASCII.GetBytes(e.Name.Replace('θ', '['));
            name.AsSpan(0, Math.Min(8, name.Length)).CopyTo(head[5..]);
            head[13] = e.Version;
            head[14] = e.Archived ? (byte)0x80 : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(head[15..], (ushort)e.Data.Length);
            body.Write(head);
            body.Write(e.Data);
        }
        if (body.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("These items are too large for one TI-83 Plus file (64 KB at most).");
        }

        byte[] bodyBytes = body.ToArray();
        byte[] file = new byte[HeaderSize + bodyBytes.Length + 2];
        "**TI83F*"u8.CopyTo(file);
        file[8] = 0x1A;
        file[9] = 0x0A;
        byte[] commentBytes = Encoding.ASCII.GetBytes(comment);
        commentBytes.AsSpan(0, Math.Min(42, commentBytes.Length)).CopyTo(file.AsSpan(11));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(53), (ushort)bodyBytes.Length);
        bodyBytes.CopyTo(file, HeaderSize);
        ushort sum = 0;
        foreach (byte b in bodyBytes)
        {
            sum += b;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(HeaderSize + bodyBytes.Length), sum);
        return file;
    }

    /// <summary>
    /// A TI-83 Plus / TI-84 Plus program file (.8xp) holding <paramref name="tokens"/> under
    /// <paramref name="name"/> (see <see cref="TiBasic.IsValidProgramName"/>).
    /// </summary>
    public static byte[] WriteProgram(string name, ReadOnlySpan<byte> tokens, bool protectedProgram = false)
    {
        if (!TiBasic.IsValidProgramName(name))
        {
            throw new ArgumentException($"\"{name}\" isn't a valid program name.", nameof(name));
        }
        if (tokens.Length > ushort.MaxValue - 2)
        {
            throw new ArgumentException("The program is too large (64 KB at most).", nameof(tokens));
        }
        byte[] data = new byte[tokens.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)tokens.Length);
        tokens.CopyTo(data.AsSpan(2));
        return Write([new TiVarEntry(name, protectedProgram ? (byte)0x06 : (byte)0x05, false, data)]);
    }

    /// <summary>
    /// The name as the calculator shows it. Lists, matrices, equations, strings, pictures and GDBs
    /// are named by tokens (5D 00 = L₁); custom lists are ʟ followed by letters; others are letters,
    /// digits and θ.
    /// </summary>
    private static string DecodeName(byte type, ReadOnlySpan<byte> raw)
    {
        // A token name's second byte may be 00 (5D 00 = L₁, AA 00 = Str1), so the terminating 00 is
        // searched for after it.
        int skip = raw.Length >= 2 && TiBasic.IsPrefix(raw[0]) ? 2 : 0;
        int end = raw[skip..].IndexOf((byte)0);
        raw = raw[..(end < 0 ? raw.Length : skip + end)];
        if (raw.Length >= 2 && raw[0] == 0x5D && raw[1] >= 0x06)
        {
            return "ʟ" + Letters(raw[1..]); // custom list
        }
        if (skip == 2)
        {
            return TiBasic.Detokenize(raw[..2]) + Letters(raw[2..]);
        }
        if (raw.Length == 0 || raw[0] < 0x20)
        {
            // System variables carry no usable name; use what the calculator's link list shows.
            return type switch
            {
                0x0F => "Window",
                0x10 => "RclWindw",
                0x11 => "TblSet",
                _ => Letters(raw),
            };
        }
        return Letters(raw);

        static string Letters(ReadOnlySpan<byte> bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
            {
                sb.Append(b == 0x5B ? 'θ' : b is >= 0x20 and < 0x7F ? (char)b : '?');
            }
            return sb.ToString();
        }
    }

    /// <summary>tilibs type codes for the TI-83 Plus family (tifiles_vartype2string).</summary>
    public static string TypeCodeOf(byte typeId) => typeId switch
    {
        0x00 => "REAL",
        0x01 => "LIST",
        0x02 => "MAT",
        0x03 or 0x0B => "EQU",
        0x04 => "STR",
        0x05 => "PRGM",
        0x06 => "PPRGM",
        0x07 => "PIC",
        0x08 => "GDB",
        0x0C => "CPLX",
        0x0D => "CLIST",
        0x0F => "WINDW",
        0x10 => "ZSTO",
        0x11 => "TABLE",
        0x15 => "APPV",
        0x17 => "GRP",
        0x1A => "IMAGE",
        _ => $"{typeId:X2}",
    };

    /// <summary>Extension of a file holding one variable of this type (TI-83 Plus family).</summary>
    public static string ExtensionOf(byte typeId) => typeId switch
    {
        0x00 => ".8xn",
        0x01 or 0x0D => ".8xl",
        0x02 => ".8xm",
        0x03 or 0x0B => ".8xy",
        0x04 => ".8xs",
        0x05 or 0x06 => ".8xp",
        0x07 => ".8xi",
        0x08 => ".8xd",
        0x0C => ".8xc",
        0x0F => ".8xw",
        0x10 => ".8xz",
        0x11 => ".8xt",
        0x15 => ".8xv",
        0x17 => ".8xg",
        0x1A => ".8ca",
        _ => ".8x" + (typeId & 0x0F).ToString("x"),
    };
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
