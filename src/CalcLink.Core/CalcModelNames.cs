namespace CalcLink.Core;

/// <summary>Display names and backup formats per model, shared by the app and the command-line tool.</summary>
public static class CalcModelNames
{
    /// <summary>The model's name as CalcLink shows it (also the backups subfolder name).</summary>
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

    /// <summary>
    /// Extension (without dot) of a native backup for <paramref name="model"/>, or null when the model
    /// only supports TIGroup backups.
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
    /// The extension "Back up now" uses for <paramref name="model"/> (".tig" on flash models, else the
    /// native backup), or null when the model has neither.
    /// </summary>
    public static string? BackupExtension(CalcModel model) =>
        Backups.UsesTiGroup(model) ? ".tig" : NativeBackupExtension(model) is { } ext ? "." + ext : null;
}
