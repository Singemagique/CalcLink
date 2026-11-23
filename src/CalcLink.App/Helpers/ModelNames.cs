using CalcLink.Core;

namespace CalcLink.App.Helpers;

/// <summary>Display names and file-type knowledge the UI needs about calculator models.</summary>
public static class ModelNames
{
    public static string Of(CalcModel model) => CalcModelNames.Of(model);

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

    /// <summary>Extension (without dot) of a native backup, or null (see <see cref="CalcModelNames"/>).</summary>
    public static string? NativeBackupExtension(CalcModel model) => CalcModelNames.NativeBackupExtension(model);

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
