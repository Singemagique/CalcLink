// Shared contract between CalcLink.Core and CalcLink.App. Values mirror tilibs enums
// (see native/include/calclink.h). Changing these is a breaking change for both sides.

namespace CalcLink.Core;

/// <summary>tilibs CalcModel (libticonv ticonv.h). Numeric values must match.</summary>
public enum CalcModel
{
    None = 0,
    TI73, TI82, TI83, TI83P, TI84P, TI85, TI86,
    TI89, TI89T, TI92, TI92P, V200,
    TI84P_USB, TI89T_USB, Nspire, TI80,
    TI84PC, TI84PC_USB, TI83PCE_USB, TI84PCE_USB, TI82A_USB, TI84PT_USB,
}

/// <summary>tilibs CableModel (libticables ticables.h). Numeric values must match.</summary>
public enum CableModel
{
    Null = 0,
    GrayLink, BlackLink, Parallel, SilverLink, DirectLink,
    VirtualTi, TiEmu, Ilp, Dev,
    TcpClient, TcpServer,
}

/// <summary>tilibs CalcFeatures bits (libticalcs ticalcs.h).</summary>
[Flags]
public enum CalcFeatures : uint
{
    None = 0,
    IsReady   = 1u << 0,
    Keys      = 1u << 1,
    Screen    = 1u << 2,
    DirList   = 1u << 3,
    Backup    = 1u << 4,
    Vars      = 1u << 5,
    Flash     = 1u << 6,
    IdList    = 1u << 7,
    Clock     = 1u << 8,
    RomDump   = 1u << 9,
    Version   = 1u << 10,
    NewFolder = 1u << 11,
    DeleteVar = 1u << 12,
    Os        = 1u << 13,
    Rename    = 1u << 14,
    Silent    = 1u << 15,
    Folders   = 1u << 16,
    MemFree   = 1u << 17,
    FlashApps = 1u << 18,
    Cert      = 1u << 19,
    BackupFts = 1u << 20,
    ChangeAttr = 1u << 21,
    NonSilent = 1u << 22,
}

public enum VariableAttribute : byte { None = 0, Locked = 1, Protected = 2, Archived = 3 }

public enum TiFileClass { Unknown = 0, Variable, App, Os, Backup, TiGroup, Certificate }

/// <summary>A calculator (or cable) found during discovery.</summary>
public sealed record DeviceDescriptor(
    string DisplayName, CableModel Cable, int Port, CalcModel Model, ushort VendorId, ushort ProductId);

/// <summary>Identity and capability information read from the calculator.</summary>
/// <remarks>
/// Memory values are in bytes: <c>RamUser</c>/<c>FlashUser</c> are the user-available totals and
/// <c>*Free</c> the free amounts. 0 means "unknown/not reported" (tilibs leaves unreported fields 0).
/// Nullable booleans are null when the calculator did not report that field.
/// <c>RunLevel</c>: 1 = boot code, 2 = OS, 0 = unknown. Empty strings mean "not reported".
/// </remarks>
public sealed record CalcInfo(
    CalcModel Model,
    string ProductName,
    string ProductId,
    string OsVersion,
    string BootVersion,
    ulong RamUser, ulong RamFree, ulong FlashUser, ulong FlashFree,
    int LcdWidth, int LcdHeight, int BitsPerPixel,
    bool? BatteryOk, bool? ColorScreen, bool? PythonOnBoard, bool? ExactMath, bool? ClockSupport,
    int RunLevel);

/// <summary>A variable, program or app stored on the calculator.</summary>
public sealed class CalcVariable
{
    public required string Folder { get; init; }
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public required string FileExtension { get; init; }
    public required uint Size { get; init; }
    public required byte RawType { get; init; }
    public required VariableAttribute Attribute { get; init; }
    public required bool IsApp { get; init; }

    /// <summary>Backend-specific handle (native VarEntry, or simulator data). Do not touch in the UI.</summary>
    public object? BackendToken { get; init; }

    public string FullName => string.IsNullOrEmpty(Folder) ? Name : $"{Folder}\\{Name}";
    public override string ToString() => FullName;
}

/// <summary>Progress of a transfer. <c>RateKbps</c> is in kilobytes per second (KB/s), 0 if unknown.</summary>
public sealed record TransferProgress(long Current, long Total, double RateKbps, string Status)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Current / Total, 0, 1) : null;
}

/// <summary>Screenshot pixels as BGRA8 (stride = Width*4), ready for a WriteableBitmap.
/// Alpha is always 255 (opaque), so premultiplied and straight alpha are identical.</summary>
public sealed record ScreenImage(int Width, int Height, byte[] Bgra);

/// <summary>Calculator clock.</summary>
/// <param name="Value">Local date/time on the calculator (DateTimeKind.Unspecified).</param>
/// <param name="TimeFormat">12 or 24 (hour display mode).</param>
/// <param name="DateFormat">Opaque model-specific date display code from tilibs
/// (see ticalcs_clock_format2date). Round-trip the value read from the calculator unless the
/// user explicitly changes it.</param>
public sealed record CalcClock(DateTime Value, int TimeFormat, int DateFormat);

/// <summary>Options for <see cref="ICalculatorService.ConnectAsync(DeviceDescriptor, ConnectOptions, CancellationToken)"/>.</summary>
/// <param name="TimeoutSeconds">Link timeout in seconds; 0 = library default.</param>
public sealed record ConnectOptions(double TimeoutSeconds = 0)
{
    public static ConnectOptions Default { get; } = new();
}

/// <summary>Thrown for any link/library failure. <see cref="Code"/> is the native code (tilibs &gt; 0, shim &lt; 0).</summary>
public class CalcLinkException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public bool IsCancellation => Code == -5;
}

public sealed class FeatureNotSupportedException(CalcFeatures feature, CalcModel model)
    : CalcLinkException(-3, $"{feature} is not supported on {model}.")
{
    public CalcFeatures Feature { get; } = feature;
}
