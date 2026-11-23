using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CalcLink.Core;

namespace CalcLink.Cli;

/// <summary>calclink-cli: CalcLink from the command line, for scripts and scheduled backups (F9).</summary>
public static class Program
{
    private const int ExitOk = 0, ExitError = 1, ExitUsage = 2, ExitNoCalculator = 3;

    private const string Usage = """
        calclink-cli: TI calculator link from the command line (part of CalcLink)

        Usage: calclink-cli <command> [arguments] [options]

        Commands
          devices                    List connected calculators.
          info                       Model, ID, OS and boot versions, free memory, battery.
          list                       List the variables, programs and apps on the calculator.
          send FILE...               Send files. If a name already exists on the calculator, add
                                     --replace (overwrite) or --skip-existing (leave it).
          receive NAME... [--to DIR] Receive variables into DIR (default: current folder).
                                     Names may use * and ?, e.g. "receive *" or "receive PRG*".
          backup [FILE]              Back up everything. Without FILE: a dated file in the same
                                     backups folder as the app (Documents\CalcLink Backups).
          screenshot FILE.png        Save the calculator screen (--scale N enlarges it).
          view NAME|FILE             Print a program as text, from the calculator or a file.

        Options
          --device N        Use the Nth calculator from "devices" (default: the only one).
          --timeout SEC     Link timeout in seconds (default 1.5).
          --simulator       Use CalcLink's simulated calculators instead of real ones.
          --help            Show this help.

        Exit codes: 0 done, 1 failed, 2 bad command line, 3 no calculator found.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8; // → ² √ θ in program listings
        Options opts;
        try
        {
            opts = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run \"calclink-cli --help\" for usage.");
            return ExitUsage;
        }
        if (opts.Help || opts.Command is null)
        {
            Console.WriteLine(Usage);
            return opts.Command is null && !opts.Help ? ExitUsage : ExitOk;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let the transfer stop cleanly
            cts.Cancel();
        };

        try
        {
            return opts.Command switch
            {
                "devices" => await DevicesAsync(opts, cts.Token),
                "info" => await WithSessionAsync(opts, InfoAsync, cts.Token),
                "list" or "ls" => await WithSessionAsync(opts, ListAsync, cts.Token),
                "send" => await SendAsync(opts, cts.Token),
                "receive" or "recv" => await WithSessionAsync(opts, ReceiveAsync, cts.Token),
                "backup" => await WithSessionAsync(opts, BackupAsync, cts.Token),
                "screenshot" or "screen" => await WithSessionAsync(opts, ScreenshotAsync, cts.Token),
                "view" => await ViewAsync(opts, cts.Token),
                _ => throw new UsageException($"Unknown command \"{opts.Command}\"."),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }
        catch (NoCalculatorException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitNoCalculator;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitError;
        }
        catch (Exception ex) when (ex is CalcLinkException or IOException or UnauthorizedAccessException
                                       or FormatException or InvalidOperationException or DllNotFoundException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitError;
        }
    }

    // --- Connection ------------------------------------------------------------------------------

    private static ICalculatorService CreateService(Options opts)
    {
        if (opts.Simulator)
        {
            return CalculatorServiceFactory.Create(CalculatorBackend.Simulator);
        }
        if (!CalculatorServiceFactory.IsNativeAvailable)
        {
            throw new InvalidOperationException(
                $"The calculator backend isn't available: {CalculatorServiceFactory.NativeUnavailableReason}");
        }
        return CalculatorServiceFactory.Create(CalculatorBackend.Native);
    }

    private static async Task<int> DevicesAsync(Options opts, CancellationToken ct)
    {
        var devices = await CreateService(opts).ProbeAsync(ct);
        if (devices.Count == 0)
        {
            throw new NoCalculatorException();
        }
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            Console.WriteLine($"{i + 1}  {d.DisplayName}  ({CalcModelNames.Of(d.Model)}, USB {d.VendorId:X4}:{d.ProductId:X4})");
        }
        return ExitOk;
    }

    private static async Task<int> WithSessionAsync(Options opts, Func<Options, ICalculatorSession, CancellationToken, Task<int>> work,
        CancellationToken ct)
    {
        var service = CreateService(opts);
        var devices = await service.ProbeAsync(ct);
        if (devices.Count == 0)
        {
            throw new NoCalculatorException();
        }
        DeviceDescriptor device;
        if (opts.Device is { } n)
        {
            if (n < 1 || n > devices.Count)
            {
                throw new UsageException($"--device {n}: there {(devices.Count == 1 ? "is 1 calculator" : $"are {devices.Count} calculators")}.");
            }
            device = devices[n - 1];
        }
        else if (devices.Count == 1)
        {
            device = devices[0];
        }
        else
        {
            throw new UsageException($"{devices.Count} calculators are connected; choose one with --device N (see \"calclink-cli devices\").");
        }

        await using var session = await service.ConnectAsync(device, new ConnectOptions(opts.TimeoutSeconds), ct);
        return await work(opts, session, ct);
    }

    // --- Commands --------------------------------------------------------------------------------

    private static async Task<int> InfoAsync(Options opts, ICalculatorSession session, CancellationToken ct)
    {
        var i = await session.GetInfoAsync(ct);
        string id = string.IsNullOrEmpty(i.CalcId) ? i.ProductId : i.CalcId;
        Row("Model", string.IsNullOrEmpty(i.ProductName) ? CalcModelNames.Of(i.Model) : i.ProductName);
        Row("ID", CalcIdentity.FormatId(id));
        Row("OS version", i.OsVersion);
        Row("Boot version", i.BootVersion);
        if (i.RamFree > 0 || i.RamUser > 0)
        {
            Row("RAM free", Bytes(i.RamFree) + (i.RamUser > 0 ? $" of {Bytes(i.RamUser)}" : string.Empty));
        }
        if (i.FlashFree > 0 || i.FlashUser > 0)
        {
            Row("Archive free", Bytes(i.FlashFree) + (i.FlashUser > 0 ? $" of {Bytes(i.FlashUser)}" : string.Empty));
        }
        Row("Battery", i.BatteryOk switch { true => "OK", false => "LOW", null => string.Empty });
        return ExitOk;

        static void Row(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Console.WriteLine($"{label,-13}{value}");
            }
        }
    }

    private static async Task<int> ListAsync(Options opts, ICalculatorSession session, CancellationToken ct)
    {
        var vars = await session.GetVariablesAsync(ct);
        foreach (var v in vars.OrderBy(v => v.IsApp).ThenBy(v => v.FullName, StringComparer.OrdinalIgnoreCase))
        {
            string where = v.IsApp ? "app" : v.Attribute == VariableAttribute.Archived ? "archive" : "RAM";
            Console.WriteLine($"{v.FullName,-20} {v.TypeName.Trim(),-6} {v.Size,8}  {where}");
        }
        Console.Error.WriteLine($"{vars.Count} item(s)");
        return ExitOk;
    }

    private static async Task<int> SendAsync(Options opts, CancellationToken ct)
    {
        if (opts.Positional.Count == 0)
        {
            throw new UsageException("send: name at least one file.");
        }
        var files = new List<string>();
        foreach (string p in opts.Positional)
        {
            string full = Path.GetFullPath(p);
            if (!File.Exists(full))
            {
                throw new UsageException($"send: {p} doesn't exist.");
            }
            files.Add(full);
        }
        return await WithSessionAsync(opts, async (o, session, token) =>
        {
            var service = CreateService(o);
            if (files.FirstOrDefault(f => service.ClassifyFile(f, out _) == TiFileClass.Os) is { } os)
            {
                Console.Error.WriteLine($"{Path.GetFileName(os)} is an operating system. Send OS updates from the CalcLink app (Tools → Send OS), which checks the battery first.");
                return ExitError;
            }

            // Name clashes: the calculator overwrites silently, so make the caller choose.
            var existing = await session.GetVariablesAsync(token);
            var clashes = VariableConflicts.Find(session.Model, existing, files.Select(f => (f, session.GetFileVariables(f))));
            if (clashes.Count > 0 && !o.Replace)
            {
                if (!o.SkipExisting)
                {
                    Console.Error.WriteLine("These already exist on the calculator:");
                    foreach (var c in clashes)
                    {
                        Console.Error.WriteLine($"  {c.Existing.FullName} (from {Path.GetFileName(c.Path)})");
                    }
                    Console.Error.WriteLine("Add --replace to overwrite them, or --skip-existing to leave them.");
                    return ExitError;
                }
                var skip = clashes.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files.Where(skip.Contains))
                {
                    Console.Error.WriteLine($"skipped {Path.GetFileName(f)} (already on the calculator)");
                }
                files = files.Where(f => !skip.Contains(f)).ToList();
            }
            if (files.Count == 0)
            {
                return ExitOk;
            }
            await session.SendFilesAsync(files, new ConsoleProgress(), token);
            ConsoleProgress.Finish();
            Console.WriteLine($"sent {files.Count} file(s)");
            return ExitOk;
        }, ct);
    }

    private static async Task<int> ReceiveAsync(Options opts, ICalculatorSession session, CancellationToken ct)
    {
        if (opts.Positional.Count == 0)
        {
            throw new UsageException("receive: name at least one variable (or \"*\" for everything).");
        }
        var vars = await session.GetVariablesAsync(ct);
        var patterns = opts.Positional.Select(Wildcard).ToList();
        var chosen = vars.Where(v => patterns.Any(p => p.IsMatch(v.Name) || p.IsMatch(v.FullName))).ToList();
        if (chosen.Count == 0)
        {
            Console.Error.WriteLine("Nothing on the calculator matches " + string.Join(" ", opts.Positional) + ".");
            return ExitError;
        }
        string folder = Path.GetFullPath(opts.To ?? ".");
        var written = await session.ReceiveAsync(chosen, folder, new ConsoleProgress(), ct);
        ConsoleProgress.Finish();
        foreach (string w in written)
        {
            Console.WriteLine(w);
        }
        return ExitOk;
    }

    private static async Task<int> BackupAsync(Options opts, ICalculatorSession session, CancellationToken ct)
    {
        string path;
        if (opts.Positional.Count > 0)
        {
            path = Path.GetFullPath(opts.Positional[0]);
        }
        else
        {
            string ext = CalcModelNames.BackupExtension(session.Model)
                         ?? throw new InvalidOperationException($"The {CalcModelNames.Of(session.Model)} can't be backed up.");
            var info = await session.GetInfoAsync(ct);
            string folder = Backups.CalculatorFolder(BackupRoot(), CalcModelNames.Of(session.Model), info.CalcId);
            Directory.CreateDirectory(folder);
            path = Backups.NewBackupPath(folder, DateTime.Now, ext);
        }
        await session.BackupAsync(path, new ConsoleProgress(), ct);
        ConsoleProgress.Finish();
        int? items = path.EndsWith(".tig", StringComparison.OrdinalIgnoreCase) ? Backups.CountEntries(path) : null;
        Console.WriteLine(path);
        Console.Error.WriteLine(items is { } n ? $"backed up {n} item(s)" : "backed up");
        return ExitOk;
    }

    private static async Task<int> ScreenshotAsync(Options opts, ICalculatorSession session, CancellationToken ct)
    {
        if (opts.Positional.Count != 1)
        {
            throw new UsageException("screenshot: name the .png file to write.");
        }
        var image = await session.CaptureScreenAsync(ct);
        string path = Path.GetFullPath(opts.Positional[0]);
        await File.WriteAllBytesAsync(path, ScreenPng.Encode(image, opts.Scale), ct);
        Console.WriteLine(path);
        return ExitOk;
    }

    private static async Task<int> ViewAsync(Options opts, CancellationToken ct)
    {
        if (opts.Positional.Count != 1)
        {
            throw new UsageException("view: name one program, or one program file.");
        }
        string arg = opts.Positional[0];
        if (File.Exists(arg))
        {
            return PrintPrograms(TiVarFile.Read(arg));
        }
        return await WithSessionAsync(opts, async (_, session, token) =>
        {
            var vars = await session.GetVariablesAsync(token);
            var v = vars.FirstOrDefault(x => !x.IsApp && string.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase));
            if (v is null)
            {
                Console.Error.WriteLine($"No variable called {arg} on the calculator, and no file by that name.");
                return ExitError;
            }
            string temp = Path.Combine(Path.GetTempPath(), "CalcLink", "cli", Guid.NewGuid().ToString("N"));
            try
            {
                var files = await session.ReceiveAsync([v], temp, null, token);
                return PrintPrograms(TiVarFile.Read(files[0]));
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
            }
        }, ct);
    }

    private static int PrintPrograms(IReadOnlyList<TiVarEntry> entries)
    {
        var programs = entries.Where(e => e.IsProgram).ToList();
        if (programs.Count == 0)
        {
            Console.Error.WriteLine("No program in it.");
            return ExitError;
        }
        foreach (var e in programs)
        {
            var listing = ProgramListing.From(e.Name, e.ProgramTokens!);
            if (programs.Count > 1)
            {
                Console.WriteLine($"=== {e.Name} ===");
            }
            if (listing.Kind == ProgramKind.Assembly)
            {
                Console.Error.WriteLine($"{e.Name} is an assembly program ({listing.ByteCount} bytes of machine code); hex dump:");
            }
            Console.WriteLine(listing.Text);
        }
        return ExitOk;
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>The app's backups root: Settings → Backups folder, else Documents\CalcLink Backups.</summary>
    private static string BackupRoot()
    {
        try
        {
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalcLink", "settings.json");
            if (File.Exists(settings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("BackupFolder", out var f) && f.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(f.GetString()))
                {
                    return f.GetString()!;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings: use the default.
        }
        return Backups.DefaultRoot;
    }

    private static Regex Wildcard(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Bytes(ulong n) => n switch
    {
        >= 1024 * 1024 => $"{n / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{n / 1024.0:0.#} KB",
        _ => $"{n} B",
    };

    private sealed class UsageException(string message) : Exception(message);

    private sealed class NoCalculatorException()
        : Exception("No calculator found. Check the cable, switch the calculator on, and see \"Calculator not found?\" in the CalcLink app.");

    /// <summary>One self-updating progress line on stderr (nothing when stderr is redirected).</summary>
    private sealed class ConsoleProgress : IProgress<TransferProgress>
    {
        private static bool s_drawn;

        public void Report(TransferProgress p)
        {
            if (Console.IsErrorRedirected)
            {
                return;
            }
            string pct = p.Fraction is { } f ? $"{f * 100,3:0}%" : "   ";
            string rate = p.RateKbps > 0 ? $"{p.RateKbps:0.0} KB/s" : string.Empty;
            string line = $"{pct}  {rate,-9}  {p.Status}";
            int width = Math.Max(20, Console.WindowWidth - 1);
            Console.Error.Write("\r" + (line.Length > width ? line[..width] : line.PadRight(width)));
            s_drawn = true;
        }

        public static void Finish()
        {
            if (s_drawn)
            {
                Console.Error.WriteLine();
                s_drawn = false;
            }
        }
    }

    /// <summary>Parsed command line.</summary>
    private sealed class Options
    {
        public string? Command { get; private set; }
        public List<string> Positional { get; } = [];
        public int? Device { get; private set; }
        public double TimeoutSeconds { get; private set; } = 1.5;
        public bool Simulator { get; private set; }
        public bool Replace { get; private set; }
        public bool SkipExisting { get; private set; }
        public string? To { get; private set; }
        public int Scale { get; private set; } = 1;
        public bool Help { get; private set; }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{a} needs a value.");
                switch (a.ToLowerInvariant())
                {
                    case "--help" or "-h" or "/?" or "help":
                        o.Help = true;
                        break;
                    case "--device":
                        o.Device = int.TryParse(Next(), out int d) ? d : throw new UsageException("--device needs a number.");
                        break;
                    case "--timeout":
                        o.TimeoutSeconds = double.TryParse(Next(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double t) && t is >= 0.5 and <= 60
                            ? t : throw new UsageException("--timeout needs seconds between 0.5 and 60.");
                        break;
                    case "--simulator":
                        o.Simulator = true;
                        break;
                    case "--replace":
                        o.Replace = true;
                        break;
                    case "--skip-existing":
                        o.SkipExisting = true;
                        break;
                    case "--to":
                        o.To = Next();
                        break;
                    case "--scale":
                        o.Scale = int.TryParse(Next(), out int s) && s is >= 1 and <= 16 ? s : throw new UsageException("--scale needs 1 to 16.");
                        break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new UsageException($"Unknown option {a}.");
                        }
                        if (o.Command is null)
                        {
                            o.Command = a.ToLowerInvariant();
                        }
                        else
                        {
                            o.Positional.Add(a);
                        }
                        break;
                }
            }
            if (o.Replace && o.SkipExisting)
            {
                throw new UsageException("Use either --replace or --skip-existing, not both.");
            }
            return o;
        }
    }
}
