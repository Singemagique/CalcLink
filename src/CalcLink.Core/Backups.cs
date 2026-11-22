using System.Globalization;
using System.IO.Compression;

namespace CalcLink.Core;

/// <summary>A backup file in the backups folder.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Created">When the backup was made (from the file name, else the file's write time).</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="ItemCount">Variables and apps inside a TI group; null for a native (RAM image) backup.</param>
public sealed record BackupFile(string Path, DateTime Created, long Size, int? ItemCount)
{
    public bool IsTiGroup => string.Equals(System.IO.Path.GetExtension(Path), ".tig", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The dated-backups folder: <c>&lt;root&gt;\&lt;calculator&gt;\yyyy-MM-dd HH-mm-ss.tig</c>, one subfolder per
/// calculator so two calculators of the same model don't mix.
/// </summary>
public static class Backups
{
    private const string StampFormat = "yyyy-MM-dd HH-mm-ss";

    /// <summary>Default root: Documents\CalcLink Backups.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CalcLink Backups");

    /// <summary>
    /// Subfolder for one calculator: the model name, plus the last four digits of its ID when known
    /// (e.g. "TI-83 Plus (D5E6)"), with characters Windows doesn't allow in names replaced.
    /// </summary>
    public static string CalculatorFolder(string root, string modelName, string? calcId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string id = new string((calcId ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        string name = id.Length >= 4 ? $"{modelName} ({id[^4..]})" : modelName;
        char[] invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return Path.Combine(root, name.Length == 0 ? "Calculator" : name);
    }

    /// <summary>
    /// Whether "Back up now" should make a TI group (.tig). On flash models it is the only backup
    /// that holds archived variables and apps: their native backup (e.g. .8xb) is a RAM image.
    /// Older models (TI-82/83/85/86, 68k) keep everything in their native backup.
    /// </summary>
    public static bool UsesTiGroup(CalcModel model) => model is CalcModel.TI73 or CalcModel.TI83P or CalcModel.TI84P
        or CalcModel.TI84P_USB or CalcModel.TI84PC or CalcModel.TI84PC_USB or CalcModel.TI83PCE_USB
        or CalcModel.TI84PCE_USB or CalcModel.TI82A_USB or CalcModel.TI84PT_USB
        or CalcModel.TI89T_USB or CalcModel.Nspire;

    /// <summary>A new, unused file name in <paramref name="folder"/> for a backup made at <paramref name="when"/>.</summary>
    public static string NewBackupPath(string folder, DateTime when, string extension)
    {
        string ext = extension.StartsWith('.') ? extension : "." + extension;
        string stamp = when.ToString(StampFormat, CultureInfo.InvariantCulture);
        string path = Path.Combine(folder, stamp + ext);
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stamp} ({n}){ext}");
        }
        return path;
    }

    /// <summary>Backups in <paramref name="folder"/>, newest first. A missing folder has none.</summary>
    /// <param name="folder">One calculator's backups folder (<see cref="CalculatorFolder"/>).</param>
    /// <param name="extensions">File extensions that count as backups (".tig" plus the model's native one).</param>
    public static IReadOnlyList<BackupFile> List(string folder, IEnumerable<string> extensions)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var exts = extensions.Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<BackupFile>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            // ".partial" files are backups still being written (or abandoned by a crash).
            if (!exts.Contains(file.Extension) || file.Name.Contains(".partial", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(new BackupFile(file.FullName, CreatedOf(file), file.Length,
                file.Extension.Equals(".tig", StringComparison.OrdinalIgnoreCase) ? CountEntries(file.FullName) : null));
        }
        return result.OrderByDescending(b => b.Created).ToList();
    }

    private static DateTime CreatedOf(FileInfo file)
    {
        string stem = Path.GetFileNameWithoutExtension(file.Name);
        if (stem.Length >= StampFormat.Length
            && DateTime.TryParseExact(stem[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var when))
        {
            return when;
        }
        return file.LastWriteTime;
    }

    /// <summary>Variables and apps in a TI group, or null if it can't be read.</summary>
    public static int? CountEntries(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Count(e => e.Length > 0 && !e.FullName.EndsWith('/'));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null; // damaged or unreadable: still listed, without a count
        }
    }

    /// <summary>
    /// Extracts the variables and apps of a TI group (.tig, a ZIP archive) into <paramref name="destination"/>.
    /// Only the file name of each entry is used, so an entry can't write outside the folder.
    /// Returns the extracted files in archive order.
    /// </summary>
    public static IReadOnlyList<string> ExtractTiGroup(string tigPath, string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination);
        var files = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(tigPath);
        foreach (var entry in zip.Entries)
        {
            string name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (name.Length == 0 || entry.Length == 0)
            {
                continue;
            }
            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Two entries with the same name (different case, say) mustn't overwrite each other.
            for (int n = 2; !used.Add(target); n++)
            {
                target = Path.Combine(root, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            }
            entry.ExtractToFile(target, overwrite: true);
            files.Add(target);
        }
        return files;
    }
}
