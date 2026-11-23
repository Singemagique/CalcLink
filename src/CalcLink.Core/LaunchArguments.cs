namespace CalcLink.Core;

/// <summary>What CalcLink was asked to do on its command line (Explorer "Open" / "Send to calculator").</summary>
/// <param name="Send">--send: send <paramref name="Files"/> to the calculator.</param>
/// <param name="Files">Existing files named on the command line, full paths, no duplicates.</param>
public sealed record LaunchArguments(bool Send, IReadOnlyList<string> Files)
{
    public static LaunchArguments None { get; } = new(false, []);

    /// <summary>
    /// The file to show on the Files page, when the request is a plain "Open" of exactly one file the
    /// Files page can read. Otherwise null: files are queued to be sent.
    /// </summary>
    public string? FileToOpen => !Send && Files.Count == 1 && GroupFile.CanOpen(Files[0]) ? Files[0] : null;

    /// <summary>
    /// Parses arguments: <c>--send</c> (or <c>/send</c>) switches to sending; arguments naming existing
    /// files are kept; anything else (other switches, the program's own path) is ignored.
    /// </summary>
    public static LaunchArguments Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool send = false;
        var files = new List<string>();
        foreach (string raw in args)
        {
            string arg = raw.Trim().Trim('"');
            if (arg.Length == 0)
            {
                continue;
            }
            if (arg.Equals("--send", StringComparison.OrdinalIgnoreCase) || arg.Equals("/send", StringComparison.OrdinalIgnoreCase))
            {
                send = true;
                continue;
            }
            if (arg.StartsWith('-') || (arg.StartsWith('/') && !arg.StartsWith("//", StringComparison.Ordinal)))
            {
                continue;
            }
            try
            {
                string full = Path.GetFullPath(arg);
                if (File.Exists(full) && !IsProgram(full) && !files.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                // Not a usable path.
            }
        }
        return new LaunchArguments(send, files);
    }

    /// <summary>Parses a whole command line, as a redirected activation delivers it.</summary>
    public static LaunchArguments Parse(string commandLine) => Parse(Split(commandLine));

    /// <summary>
    /// Splits a Windows command line into arguments: spaces separate, double quotes group. Paths
    /// can't contain quotes, so the backslash-escaping rules aren't needed for file names.
    /// </summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return args;
        }
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
        {
            args.Add(current.ToString());
        }
        return args;
    }

    // A redirected command line may start with CalcLink's own path.
    private static bool IsProgram(string path) =>
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
}
