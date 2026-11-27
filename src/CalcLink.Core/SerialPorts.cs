using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace CalcLink.Core;

/// <summary>The PC's serial (COM) ports, for the GrayLink cable.</summary>
public static partial class SerialPorts
{
    /// <summary>
    /// The COM ports Windows lists under HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (real ports and USB-serial
    /// adapters that are plugged in), sorted by number, e.g. ["COM1", "COM4"]. Empty if none or not Windows.
    /// </summary>
    public static IReadOnlyList<string> GetNames() => OperatingSystem.IsWindows() ? ReadRegistry() : [];

    [SupportedOSPlatform("windows")]
    private static string[] ReadRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null)
                return [];
            var names = new List<string>();
            foreach (string value in key.GetValueNames())
            {
                if (Normalize(key.GetValue(value) as string) is { } port)
                    names.Add(port);
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Number).ToArray();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>"com7", "\\.\COM7" or " COM7 " → "COM7"; null for anything that isn't COM1..COM255.</summary>
    public static string? Normalize(string? name)
    {
        if (name is null)
            return null;
        Match m = PortPattern().Match(name.Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].ValueSpan, out int n) || n is < 1 or > 255)
            return null;
        return $"COM{n}";
    }

    /// <summary>The port number of a normalized name ("COM12" → 12).</summary>
    public static int Number(string name) => int.TryParse(name.AsSpan(3), out int n) ? n : int.MaxValue;

    // An optional "\\.\" prefix, then COM and 1 to 3 digits.
    [GeneratedRegex(@"^(?:\\\\\.\\)?COM(\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();
}
