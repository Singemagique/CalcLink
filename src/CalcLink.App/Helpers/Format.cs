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

    /// <summary>"12 KB free of 24 KB", or "12 KB free" when the total isn't reported (TI-83 Plus).</summary>
    public static string Memory(ulong free, ulong total) =>
        total > 0 ? $"{Bytes(free)} free of {Bytes(total)}" : $"{Bytes(free)} free";

    public static string Rate(double kbps) =>
        kbps <= 0 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $"{kbps:0.0} KB/s");

    /// <summary>
    /// One-line message for any exception, including the native code for <see cref="CalcLinkException"/>.
    /// For inline text (InfoBars, page banners); dialogs use <see cref="ErrorMessage"/> plus
    /// <see cref="ErrorDetails"/> so the code appears only once.
    /// </summary>
    public static string Error(Exception ex) => ex switch
    {
        FeatureNotSupportedException f => f.Message,
        CalcLinkException c => $"{c.Message} (error code {c.Code})",
        _ => ErrorMessage(ex),
    };

    /// <summary>The message alone, without the error code.</summary>
    public static string ErrorMessage(Exception ex) => ex switch
    {
        NotImplementedException => $"{ex.Message} This part of CalcLink is not implemented yet.",
        _ => ex.Message,
    };

    /// <summary>Small "Details" line for dialogs (error code or exception type); null if there is nothing useful.</summary>
    public static string? ErrorDetails(Exception ex) => ex switch
    {
        FeatureNotSupportedException => null,
        CalcLinkException { Code: > 0 } c => $"Details: tilibs error {c.Code}",
        CalcLinkException c => $"Details: CalcLink error {c.Code}",
        NotImplementedException => null,
        _ => $"Details: {ex.GetType().Name}",
    };

    /// <summary>"~1:20 left" style estimate.</summary>
    public static string TimeLeft(TimeSpan remaining)
    {
        double s = remaining.TotalSeconds;
        if (s < 10)
        {
            return "a few seconds left";
        }
        // Round up to 5 s so the text doesn't flicker on every report.
        long total = (long)Math.Ceiling(s / 5) * 5;
        if (total < 60)
        {
            return string.Create(CultureInfo.CurrentCulture, $"~{total} s left");
        }
        if (total < 3600)
        {
            return string.Create(CultureInfo.CurrentCulture, $"~{total / 60}:{total % 60:00} left");
        }
        return string.Create(CultureInfo.CurrentCulture, $"~{total / 3600} h {total % 3600 / 60:00} min left");
    }

    public static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException || ex is CalcLinkException { IsCancellation: true };

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfNotEmpty(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
}
