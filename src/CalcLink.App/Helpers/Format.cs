using System.Globalization;
using CalcLink.Core;
using Microsoft.UI.Xaml;

namespace CalcLink.App.Helpers;

/// <summary>Formatting helpers, also used from x:Bind function bindings.</summary>
public static class Format
{
    public static string Bytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Bytes(long bytes) => Bytes((ulong)Math.Max(0, bytes));

    public static string Rate(double kbps) =>
        kbps <= 0 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $"{kbps:0.0} KB/s");

    /// <summary>Message for any exception, including the native code for <see cref="CalcLinkException"/>.</summary>
    public static string Error(Exception ex) => ex switch
    {
        FeatureNotSupportedException f => f.Message,
        CalcLinkException c => $"{c.Message} (error code {c.Code})",
        NotImplementedException => $"{ex.Message} This part of CalcLink is not implemented yet.",
        _ => ex.Message,
    };

    public static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException || ex is CalcLinkException { IsCancellation: true };

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfNotEmpty(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
}
