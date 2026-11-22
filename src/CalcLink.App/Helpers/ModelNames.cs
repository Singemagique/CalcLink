using CalcLink.Core;

namespace CalcLink.App.Helpers;

/// <summary>Display names and file-type knowledge the UI needs about calculator models.</summary>
public static class ModelNames
{
    public static string Of(CalcModel model) => model switch
    {
        CalcModel.None => "Unknown calculator",
        CalcModel.TI73 => "TI-73",
        CalcModel.TI80 => "TI-80",
        CalcModel.TI82 => "TI-82",
        CalcModel.TI83 => "TI-83",
        CalcModel.TI83P => "TI-83 Plus",
        CalcModel.TI84P => "TI-84 Plus",
        CalcModel.TI84P_USB => "TI-84 Plus",
        CalcModel.TI85 => "TI-85",
        CalcModel.TI86 => "TI-86",
        CalcModel.TI89 => "TI-89",
        CalcModel.TI89T => "TI-89 Titanium",
        CalcModel.TI89T_USB => "TI-89 Titanium",
        CalcModel.TI92 => "TI-92",
        CalcModel.TI92P => "TI-92 Plus",
        CalcModel.V200 => "Voyage 200",
        CalcModel.Nspire => "TI-Nspire",
        CalcModel.TI84PC => "TI-84 Plus C Silver Edition",
        CalcModel.TI84PC_USB => "TI-84 Plus C Silver Edition",
        CalcModel.TI83PCE_USB => "TI-83 Premium CE",
        CalcModel.TI84PCE_USB => "TI-84 Plus CE",
        CalcModel.TI82A_USB => "TI-82 Advanced",
        CalcModel.TI84PT_USB => "TI-84 Plus T",
        _ => model.ToString(),
    };

    public static string Of(CableModel cable) => cable switch
    {
        CableModel.Null => "No cable",
        CableModel.GrayLink => "Gray TI-Graph Link (serial)",
        CableModel.BlackLink => "Black TI-Graph Link (serial)",
        CableModel.Parallel => "Parallel link",
        CableModel.SilverLink => "TI-Graph Link USB (SilverLink)",
        CableModel.DirectLink => "Direct USB",
        CableModel.VirtualTi => "Virtual TI",
        CableModel.TiEmu => "TiEmu",
        CableModel.Ilp => "Internal link port",
        CableModel.Dev => "Device",
        CableModel.TcpClient => "TCP client",
        CableModel.TcpServer => "TCP server",
        _ => cable.ToString(),
    };

    /// <summary>
    /// Extension (without dot) of a native backup for <paramref name="model"/>, or null when the model
    /// only supports TIGroup backups. TODO(contract): Core should expose this (tifiles_fext_of_backup).
    /// </summary>
    public static string? NativeBackupExtension(CalcModel model) => model switch
    {
        CalcModel.TI73 => "73b",
        CalcModel.TI82 => "82b",
        CalcModel.TI83 => "83b",
        CalcModel.TI83P or CalcModel.TI84P or CalcModel.TI84P_USB => "8xb",
        CalcModel.TI85 => "85b",
        CalcModel.TI86 => "86b",
        CalcModel.TI89 or CalcModel.TI89T or CalcModel.TI89T_USB => "89g",
        CalcModel.TI92 => "92b",
        CalcModel.TI92P => "9xg",
        CalcModel.V200 => "v2g",
        _ => null,
    };

    /// <summary>
    /// Whether "Run" (remote execution of a program or app) can work on <paramref name="model"/>.
    /// tilibs has no OPS_* bit for it; these are the models whose driver only has a no-op execute,
    /// which cl_execute (native/src/calclink.cc) reports as CL_E_NOT_SUPPORTED. Keep the two lists in sync.
    /// </summary>
    public static bool SupportsRemoteExecute(CalcModel model) =>
        model is not (CalcModel.None or CalcModel.TI80 or CalcModel.TI82 or CalcModel.TI85 or CalcModel.TI86
            or CalcModel.Nspire);

    /// <summary>Known operating-system upgrade file extensions (TI-73 ... TI-Nspire CX II).</summary>
    public static readonly IReadOnlyList<string> OsExtensions =
    [
        ".73u", ".82u", ".8xu", ".8cu", ".8eu", ".8pu", ".8yu", ".89u", ".9xu", ".v2u",
        ".tno", ".tnc", ".tco", ".tcc", ".tmo", ".tmc",
    ];
}
