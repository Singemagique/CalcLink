namespace CalcLink.Core;

/// <summary>Presents the calculator ID and product code the way the calculator's About screen does.</summary>
/// <remarks>
/// The About screen's "PROD #" (e.g. <c>04-1-01-10</c> on a TI-83 Plus with OS 1.16) is never sent over
/// the link. Only its first field, the product code, is known for certain: it is the first byte of
/// the calculator ID on TI-73/83 Plus/84 Plus-family models. The last two fields appear to be the OS
/// version in hex; the middle field's source is unknown, so CalcLink does not rebuild PROD #.
/// </remarks>
public static class CalcIdentity
{
    /// <summary>
    /// Formats a calculator ID for display: upper-case hex, and a 14-digit Z80/68k ID grouped 5-5-4
    /// (<c>04012-3AB4C-D5E6</c>), as on the About screen. Other IDs (Nspire, TI-84 Plus USB's
    /// 10-digit form) are returned upper-cased if they are hex, otherwise unchanged.
    /// </summary>
    public static string FormatId(string? id)
    {
        string s = id?.Trim() ?? string.Empty;
        if (!IsHex(s))
        {
            return s;
        }
        s = s.ToUpperInvariant();
        return s.Length == 14 ? $"{s[..5]}-{s[5..10]}-{s[10..]}" : s;
    }

    /// <summary>
    /// The product code (first field of "PROD #", e.g. <c>04</c> = TI-83 Plus family, <c>0A</c> = TI-84 Plus,
    /// <c>13</c> = TI-84 Plus CE), or null if it can't be known for this model/ID.
    /// </summary>
    public static string? ProductCode(CalcModel model, string? id)
    {
        string s = id?.Trim() ?? string.Empty;
        return IsZ80IdFamily(model) && s.Length >= 10 && IsHex(s) ? s[..2].ToUpperInvariant() : null;
    }

    /// <summary>Models whose calculator ID starts with the product code (TI-73, TI-83 Plus and TI-84 Plus families).</summary>
    public static bool IsZ80IdFamily(CalcModel model) => model is
        CalcModel.TI73 or CalcModel.TI83P or CalcModel.TI84P or CalcModel.TI84P_USB or
        CalcModel.TI84PC or CalcModel.TI84PC_USB or CalcModel.TI83PCE_USB or CalcModel.TI84PCE_USB or
        CalcModel.TI82A_USB or CalcModel.TI84PT_USB;

    private static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);
}
