using System.Text;

namespace CalcLink.Core;

/// <summary>Builds safe Windows file names for variables received from a calculator.</summary>
public static class CalcFileNames
{
    private static readonly HashSet<string> s_reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
        "CONIN$", "CONOUT$",
    };

    /// <summary>
    /// Makes <paramref name="name"/> usable as a Windows file name component: replaces
    /// <c>&lt;&gt;:"/\|?*</c> and control characters with '_', trims trailing dots/spaces and leading
    /// spaces, prefixes reserved device names (CON, NUL, COM1, ...) with '_', and never returns "".
    /// </summary>
    public static string Sanitize(string? name)
    {
        var sb = new StringBuilder(name?.Length ?? 1);
        foreach (char c in name ?? string.Empty)
        {
            bool invalid = c < 32 || c == 127 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';
            sb.Append(invalid ? '_' : c);
        }

        string s = sb.ToString().TrimStart(' ').TrimEnd('.', ' ');
        if (s.Length == 0)
            return "_";

        // "CON", "con.txt", "NUL.8xp" are all reserved: Windows looks at the part before the first dot.
        int dot = s.IndexOf('.');
        string stem = (dot >= 0 ? s[..dot] : s).TrimEnd(' ');
        if (s_reserved.Contains(stem))
            s = "_" + s;

        // Keep well under MAX_PATH components (255 UTF-16 units).
        if (s.Length > 200)
            s = s[..200].TrimEnd('.', ' ');
        return s;
    }

    /// <summary>"&lt;name&gt;.&lt;ext&gt;" with both parts sanitized (ext may be empty).</summary>
    public static string ForVariable(string name, string extension)
    {
        string stem = Sanitize(name);
        string ext = Sanitize(extension.TrimStart('.'));
        return string.IsNullOrEmpty(extension) || ext == "_" ? stem : $"{stem}.{ext}";
    }

    /// <summary>
    /// Returns a full path in <paramref name="folder"/> for <paramref name="fileName"/> that neither
    /// exists on disk nor is in <paramref name="taken"/> (compared case-insensitively), by appending
    /// " (2)", " (3)", ... before the extension. The chosen path is added to <paramref name="taken"/>.
    /// </summary>
    public static string MakeUnique(string folder, string fileName, ISet<string> taken)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string candidate = Path.Combine(folder, fileName);
        for (int i = 2; taken.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
        taken.Add(candidate);
        return candidate;
    }

    /// <summary>A case-insensitive set suitable for <see cref="MakeUnique"/>.</summary>
    public static HashSet<string> NewPathSet() => new(StringComparer.OrdinalIgnoreCase);
}
