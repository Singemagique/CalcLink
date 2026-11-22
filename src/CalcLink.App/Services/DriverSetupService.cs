using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CalcLink.Core;

namespace CalcLink.App.Services;

/// <summary>Outcome of <see cref="DriverSetupService.InstallAsync"/>.</summary>
public sealed record DriverInstallResult(bool Succeeded, bool RestartNeeded, bool Cancelled, string Message, string Log);

/// <summary>
/// Runs native\&lt;rid&gt;\calclink-driver.exe: <c>list</c> without elevation to see which driver each TI
/// device uses, and <c>install</c> with elevation (Windows asks the user) to assign WinUSB.
/// </summary>
public sealed class DriverSetupService
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user said No to the UAC prompt

    public string? HelperPath { get; } = FindHelper();

    public bool IsAvailable => HelperPath is not null;

    public static string LogPath { get; } = Path.Combine(SettingsService.AppDataFolder, "driver-setup.log");

    private static string? FindHelper()
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => "win-x64",
        };
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "native", rid, UsbDrivers.HelperFileName),
                     Path.Combine(AppContext.BaseDirectory, UsbDrivers.HelperFileName),
                 })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    /// <summary>Connected TI calculators and cables with their current driver. Empty when the helper is missing.</summary>
    public async Task<IReadOnlyList<UsbDriverDevice>> ListAsync(CancellationToken ct = default)
    {
        if (HelperPath is null)
        {
            return [];
        }
        var psi = new ProcessStartInfo(HelperPath, "list")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(psi);
        if (process is null)
        {
            return [];
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? UsbDrivers.ParseList(output) : [];
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            return [];
        }
    }

    /// <summary>
    /// Assigns WinUSB to <paramref name="hardwareId"/>. Windows shows its administrator prompt; saying
    /// No there returns a cancelled result.
    /// </summary>
    public async Task<DriverInstallResult> InstallAsync(string hardwareId)
    {
        if (HelperPath is null)
        {
            return new(false, false, false, "The driver setup tool (calclink-driver.exe) is missing from the CalcLink folder.", "");
        }
        Directory.CreateDirectory(SettingsService.AppDataFolder);
        TryDelete(LogPath);

        var psi = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add(hardwareId);
        psi.ArgumentList.Add("--log");
        psi.ArgumentList.Add(LogPath);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new(false, false, true, "Nothing was changed: Windows' administrator prompt was declined.", "");
        }
        if (process is null)
        {
            return new(false, false, false, "Windows didn't start the driver setup tool.", "");
        }

        using (process)
        {
            await process.WaitForExitAsync();
            string log = ReadLog();
            var code = (UsbDrivers.InstallExitCode)process.ExitCode;
            return code switch
            {
                UsbDrivers.InstallExitCode.Done =>
                    new(true, false, false, "The device now uses WinUSB. Press Scan to find the calculator.", log),
                UsbDrivers.InstallExitCode.RestartNeeded =>
                    new(true, true, false, "The driver is installed, but Windows needs a restart to finish.", log),
                UsbDrivers.InstallExitCode.NotConnected =>
                    new(false, false, false, "The device was unplugged before the driver could be installed. Plug it in and try again.", log),
                UsbDrivers.InstallExitCode.NotElevated =>
                    new(false, false, false, "The driver setup tool didn't get administrator rights.", log),
                UsbDrivers.InstallExitCode.PackageFailed =>
                    new(false, false, false, "CalcLink couldn't prepare the driver package. Nothing was installed.", log),
                UsbDrivers.InstallExitCode.InstallFailed =>
                    new(false, false, false, "Windows refused to install the driver. Nothing was changed.", log),
                _ => new(false, false, false, $"The driver setup tool failed (exit code {process.ExitCode}).", log),
            };
        }
    }

    private static string ReadLog()
    {
        try
        {
            return File.Exists(LogPath) ? File.ReadAllText(LogPath, Encoding.UTF8).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An old log is harmless; the helper overwrites it.
        }
    }
}
