namespace CalcLink.Core;

/// <summary>
/// A connected TI calculator or cable as reported by <c>calclink-driver.exe list</c>, with the
/// Windows driver it currently uses.
/// </summary>
/// <param name="HardwareId">For example <c>USB\VID_0451&amp;PID_E001</c>.</param>
/// <param name="Description">Device Manager's name for it.</param>
/// <param name="Service">Driver service name (<c>WinUSB</c>, TI's driver, ...), empty when it has none.</param>
/// <param name="ProblemCode">Device Manager problem code; 0 when the device works, 28 when it has no driver.</param>
public sealed record UsbDriverDevice(string HardwareId, string Description, string Service, int ProblemCode)
{
    /// <summary>
    /// True when the device has a driver that CalcLink (tilibs over libusb-1.0) can use:
    /// WinUSB, or libusbK / libusb-win32 left over from Zadig or an old TiLP.
    /// </summary>
    public bool HasUsableDriver =>
        ProblemCode == 0 && Service.ToUpperInvariant() is "WINUSB" or "LIBUSBK" or "LIBUSB0";

    /// <summary>Short description of the current driver for the UI.</summary>
    public string DriverText => Service.Length == 0 || ProblemCode == 28
        ? "no driver"
        : Service.ToUpperInvariant() switch
        {
            "WINUSB" => "WinUSB",
            "LIBUSBK" => "libusbK",
            "LIBUSB0" => "libusb-win32",
            _ => $"another driver ({Service}), probably TI Connect's",
        };
}

/// <summary>Parses the output of the <c>calclink-driver</c> helper (native/tools/calclink-driver.c).</summary>
public static class UsbDrivers
{
    /// <summary>File name of the helper, shipped next to calclink_native.dll.</summary>
    public const string HelperFileName = "calclink-driver.exe";

    /// <summary>Exit codes of <c>calclink-driver install</c>.</summary>
    public enum InstallExitCode
    {
        Done = 0,
        Usage = 1,
        NotElevated = 2,
        NotTiDevice = 3,
        NotConnected = 4,
        PackageFailed = 5,
        InstallFailed = 6,
        RestartNeeded = 10,
    }

    /// <summary>Parses <c>list</c> output: one tab-separated line per device. Malformed lines are skipped.</summary>
    public static IReadOnlyList<UsbDriverDevice> ParseList(string output)
    {
        var devices = new List<UsbDriverDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var parts = line.Split('\t');
            if (parts.Length != 4 || !parts[0].StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(parts[3], out int problem))
            {
                continue;
            }
            devices.Add(new UsbDriverDevice(parts[0], parts[1].Trim(), parts[2].Trim(), problem));
        }
        return devices;
    }
}
