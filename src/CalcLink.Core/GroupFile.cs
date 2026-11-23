namespace CalcLink.Core;

/// <summary>One variable or app inside a group, backup or variable file.</summary>
/// <param name="Name">Name as the calculator shows it.</param>
/// <param name="TypeCode">tilibs-style type code ("PRGM", "APPL", ...).</param>
/// <param name="Size">Bytes on the calculator (variable data, or the app file's size).</param>
/// <param name="Archived">Recorded as archived in the file.</param>
/// <param name="IsApp">A Flash app (can be sent, not opened).</param>
/// <param name="FilePath">A file holding just this item, ready to send or copy.</param>
/// <param name="Entry">The parsed variable (TI-83 Plus family); null for apps and unreadable items.</param>
public sealed record GroupFileItem(string Name, string TypeCode, long Size, bool Archived, bool IsApp, string FilePath, TiVarEntry? Entry)
{
    public bool IsProgram => Entry?.IsProgram == true;
}

/// <summary>What a group, backup or variable file holds.</summary>
public sealed record GroupContents(string SourcePath, IReadOnlyList<GroupFileItem> Items)
{
    public long TotalSize => Items.Sum(i => i.Size);
}

/// <summary>
/// Opens files that hold calculator variables so they can be browsed without a calculator (F5):
/// TI groups and CalcLink backups (.tig, a ZIP of variable and app files), and TI-83 / TI-83 Plus
/// family files with one variable or a group of them (.8xg, .8xp, .83g, ...). Each item is also
/// written to <c>workFolder</c> as a file of its own, so it can be sent or saved individually.
/// </summary>
public static class GroupFile
{
    /// <summary>Extensions the Files page offers in its picker.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
    [
        ".tig", ".8xg", ".83g", ".8xp", ".83p", ".8xv", ".8xn", ".8xl", ".8xm", ".8xs", ".8xy", ".8xi",
        ".8xd", ".8xc", ".8xw", ".8xz", ".8xt", ".8ca", ".8ci",
    ];

    /// <summary>
    /// True when the Files page can show <paramref name="path"/>: a .tig, or a TI-83 / TI-83 Plus family
    /// variable or group file (checked by its header, not just its extension).
    /// </summary>
    public static bool CanOpen(string path)
    {
        try
        {
            if (string.Equals(Path.GetExtension(path), ".tig", StringComparison.OrdinalIgnoreCase))
            {
                return File.Exists(path);
            }
            using var s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[55];
            return s.Length >= 55 && s.ReadAtLeast(head, 55, throwOnEndOfStream: false) == 55 && TiVarFile.IsSupported(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <exception cref="FormatException">The file isn't one CalcLink can open.</exception>
    public static GroupContents Open(string path, string workFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(workFolder);
        if (string.Equals(Path.GetExtension(path), ".tig", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> files;
            try
            {
                files = Backups.ExtractTiGroup(path, workFolder);
            }
            catch (InvalidDataException ex)
            {
                throw new FormatException("This .tig file is damaged or isn't a TI group.", ex);
            }
            return new GroupContents(path, [.. files.SelectMany(ReadMember)]);
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (!TiVarFile.IsSupported(bytes))
        {
            throw new FormatException(
                "CalcLink can open TI groups and backups (.tig) and TI-83 / TI-83 Plus / TI-84 Plus variable and group files. This file is another kind.");
        }
        var entries = TiVarFile.Read(bytes);
        var items = new List<GroupFileItem>(entries.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            string file = UniquePath(workFolder, CalcFileNames.ForVariable(e.Name, e.FileExtension.TrimStart('.')), used);
            File.WriteAllBytes(file, TiVarFile.Write([e]));
            items.Add(ItemFor(e, file));
        }
        return new GroupContents(path, items);
    }

    // One file extracted from a .tig: a TI-83 Plus family variable, an app, or something else.
    private static IEnumerable<GroupFileItem> ReadMember(string file)
    {
        byte[] bytes = File.ReadAllBytes(file); // members are small: variables up to 64 KB, apps a few pages
        string stem = Path.GetFileNameWithoutExtension(file).Trim();
        if (bytes.AsSpan().StartsWith("**TIFL**"u8))
        {
            return [new GroupFileItem(stem, "APPL", bytes.Length, Archived: true, IsApp: true, file, Entry: null)];
        }
        if (TiVarFile.IsSupported(bytes))
        {
            try
            {
                // A .tig member holds one variable; send/save the extracted file as it is.
                return TiVarFile.Read(bytes).Select(e => ItemFor(e, file)).Take(1).ToList();
            }
            catch (FormatException)
            {
                // fall through: listed, but not readable
            }
        }
        // Another model's variable (68k, Nspire) or a damaged one: still listed so it can be saved.
        string code = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
        return [new GroupFileItem(stem, code, bytes.Length, Archived: false, IsApp: false, file, Entry: null)];
    }

    private static GroupFileItem ItemFor(TiVarEntry e, string file) =>
        new(e.Name, e.TypeCode, e.Data.Length, e.Archived, IsApp: false, file, e);

    private static string UniquePath(string folder, string fileName, HashSet<string> used)
    {
        string path = Path.Combine(folder, fileName);
        for (int n = 2; !used.Add(path) || File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}");
        }
        return path;
    }
}
