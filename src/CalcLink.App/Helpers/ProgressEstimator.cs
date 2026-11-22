using System.Text.RegularExpressions;

namespace CalcLink.App.Helpers;

/// <summary>
/// Turns the raw <see cref="CalcLink.Core.TransferProgress"/> stream of one operation into an overall
/// fraction, "item n of m" and a smoothed time-left estimate.
/// </summary>
/// <remarks>
/// Multi-item transfers prefix the status with "(i/n)". The native backend then reports the fraction
/// of the <em>current item</em> (it restarts at 0 for each item); some operations (the simulator's
/// backup) report the fraction of the whole job instead. We assume per-item until an item change
/// shows the fraction did not restart, and weight items equally (their sizes aren't known up front).
/// </remarks>
public sealed partial class ProgressEstimator
{
    /// <summary>Don't show a time estimate before this much time and progress: early numbers are noise.</summary>
    private const double MinSeconds = 2.0;
    private const double MinFraction = 0.03;

    /// <summary>Time constant of the exponential smoothing of the estimate, in seconds.</summary>
    private const double SmoothingSeconds = 3.0;

    private bool? _perItem;
    private int _lastIndex;
    private double _lastRaw;
    private double _lastOverall;
    private double? _baseTime;
    private double _baseFraction;
    private double? _smoothed;
    private double _smoothedAt;

    [GeneratedRegex(@"\s*\((\d+)/(\d+)\)")]
    private static partial Regex ItemPattern();

    /// <summary>Current item (1-based) and item count, or 0/0 when not a multi-item operation.</summary>
    public int ItemIndex { get; private set; }

    public int ItemCount { get; private set; }

    /// <summary>Overall fraction 0..1, or null while progress is indeterminate.</summary>
    public double? Fraction { get; private set; }

    /// <summary>Estimated time left, or null until it is meaningful.</summary>
    public TimeSpan? Remaining { get; private set; }

    public void Reset()
    {
        _perItem = null;
        _lastIndex = 0;
        _lastRaw = 0;
        _lastOverall = 0;
        _baseTime = null;
        _baseFraction = 0;
        _smoothed = null;
        _smoothedAt = 0;
        ItemIndex = 0;
        ItemCount = 0;
        Fraction = null;
        Remaining = null;
    }

    /// <summary>
    /// Removes the first "(i/n)" marker from <paramref name="status"/> (the shell shows "Item i of n"
    /// separately) and returns the cleaned text.
    /// </summary>
    public static string ParseItem(string status, out int index, out int count)
    {
        index = 0;
        count = 0;
        if (string.IsNullOrEmpty(status))
        {
            return status;
        }
        Match m = ItemPattern().Match(status);
        if (!m.Success
            || !int.TryParse(m.Groups[1].ValueSpan, out int i)
            || !int.TryParse(m.Groups[2].ValueSpan, out int n)
            || n < 2 || i < 1 || i > n)
        {
            return status;
        }
        index = i;
        count = n;
        return status.Remove(m.Index, m.Length);
    }

    /// <param name="rawFraction">The reported fraction (null = indeterminate).</param>
    /// <param name="index">1-based item index from <see cref="ParseItem"/>, 0 if none.</param>
    /// <param name="count">Item count from <see cref="ParseItem"/>, 0 if none.</param>
    /// <param name="now">Seconds since the operation started.</param>
    public void Update(double? rawFraction, int index, int count, double now)
    {
        if (count > 1)
        {
            ItemIndex = index;
            ItemCount = count;
        }

        if (rawFraction is not { } raw)
        {
            Fraction = null;
            Remaining = null;
            return;
        }

        if (count > 1 && index >= 1)
        {
            if (_perItem is null && _lastIndex > 0 && index != _lastIndex)
            {
                if (raw < _lastRaw - 0.2)
                {
                    _perItem = true;
                }
                else if (raw >= _lastRaw - 0.02)
                {
                    // The fraction carried on across the item change: it is already overall.
                    _perItem = false;
                    _baseTime = null; // the earlier per-item guess skewed the baseline
                    _smoothed = null;
                }
            }
            _lastIndex = index;
            _lastRaw = raw;
        }

        double overall = count > 1 && index >= 1 && _perItem != false
            ? (index - 1 + raw) / count
            : raw;
        overall = Math.Clamp(Math.Max(overall, _lastOverall), 0, 1);
        _lastOverall = overall;
        Fraction = overall;

        if (_baseTime is null)
        {
            _baseTime = now;
            _baseFraction = overall;
            Remaining = null;
            return;
        }

        double elapsed = now - _baseTime.Value;
        double done = overall - _baseFraction;
        if (elapsed < MinSeconds || overall < MinFraction || done <= 0.005)
        {
            Remaining = null;
            return;
        }

        // Average rate since the first determinate report, then smoothed over time so the text
        // doesn't jump about between reports.
        double estimate = elapsed * (1 - overall) / done;
        if (_smoothed is not { } previous)
        {
            _smoothed = estimate;
        }
        else
        {
            double dt = Math.Max(0, now - _smoothedAt);
            double aged = Math.Max(0, previous - dt);
            double alpha = 1 - Math.Exp(-dt / SmoothingSeconds);
            _smoothed = aged + (alpha * (estimate - aged));
        }
        _smoothedAt = now;
        Remaining = TimeSpan.FromSeconds(Math.Max(0, _smoothed.Value));
    }
}
