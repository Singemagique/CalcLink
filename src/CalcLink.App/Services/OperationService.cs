using System.Diagnostics;
using CalcLink.App.Helpers;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.Services;

public enum OperationOutcome { Succeeded, Cancelled, Failed, Busy }

/// <summary>
/// Runs long link operations one at a time and drives the shell's transfer panel
/// (status, determinate/indeterminate progress, item n of m, rate, time left, Cancel) and the taskbar
/// button's progress. Errors are shown in a dialog, cancellations as a "Cancelled" notification,
/// except when the link was lost: the shell reports that once (see <see cref="CancelForLinkLoss"/>).
/// </summary>
public sealed partial class OperationService : ObservableObject
{
    /// <summary>How long a failed operation waits for the shell's "connection lost" before showing its own dialog.</summary>
    private static readonly TimeSpan LinkLossGrace = TimeSpan.FromMilliseconds(750);

    private readonly DialogService _dialogs;
    private readonly NotificationService _notifications;
    private readonly TaskbarProgress _taskbar;
    private readonly ProgressEstimator _estimator = new();
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _linkLost;
    private bool _acceptProgress;

    public OperationService(DialogService dialogs, NotificationService notifications, WindowService window)
    {
        _dialogs = dialogs;
        _notifications = notifications;
        _taskbar = new TaskbarProgress(() => window.Hwnd);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Title of the running operation ("Sending PRGM1"), empty when idle.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; } = true;

    /// <summary>0..100, over the whole operation (all items).</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial string RateText { get; private set; } = string.Empty;

    /// <summary>"Item 2 of 5" for multi-item transfers, else empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial string ItemText { get; private set; } = string.Empty;

    /// <summary>"~1:20 left" once the estimate is meaningful, else empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial string TimeLeftText { get; private set; } = string.Empty;

    /// <summary>"Item 2 of 5 · 1.0 KB/s · ~1:20 left" (the parts that are known).</summary>
    public string DetailText => string.Join(" · ",
        new[] { ItemText, RateText, TimeLeftText }.Where(s => !string.IsNullOrEmpty(s)));

    public event EventHandler? BusyChanged;

    partial void OnIsBusyChanged(bool value) => BusyChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        StatusText = "Cancelling…";
        TimeLeftText = string.Empty;
        _taskbar.SetState(TaskbarProgressState.Paused);
        _cts?.Cancel();
    }

    /// <summary>
    /// Cancels the running operation because the link is gone. Its outcome is not reported (no
    /// "was cancelled" notice, no error dialog): the caller tells the user about the lost link instead.
    /// </summary>
    public void CancelForLinkLoss()
    {
        if (!IsBusy)
        {
            return;
        }
        _linkLost?.TrySetResult();
        Cancel();
    }

    public async Task<OperationOutcome> RunAsync(string title,
        Func<IProgress<TransferProgress>, CancellationToken, Task> work, bool reportErrors = true)
    {
        var (outcome, _) = await RunAsync<object?>(title, async (p, ct) =>
        {
            await work(p, ct);
            return null;
        }, reportErrors);
        return outcome;
    }

    public async Task<(OperationOutcome Outcome, T? Result)> RunAsync<T>(string title,
        Func<IProgress<TransferProgress>, CancellationToken, Task<T>> work, bool reportErrors = true)
    {
        if (IsBusy)
        {
            _notifications.Post("Busy", "Another operation is still running. Wait for it to finish or cancel it.",
                InfoBarSeverity.Warning);
            return (OperationOutcome.Busy, default);
        }

        using var cts = new CancellationTokenSource();
        var linkLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = cts;
        _linkLost = linkLost;
        _estimator.Reset();
        _clock.Restart();
        Title = title;
        StatusText = title + "…";
        IsIndeterminate = true;
        ProgressValue = 0;
        RateText = string.Empty;
        ItemText = string.Empty;
        TimeLeftText = string.Empty;
        IsBusy = true;
        _acceptProgress = true;
        _taskbar.SetState(TaskbarProgressState.Indeterminate);

        // Created on the UI thread, so reports are marshalled back to it.
        var progress = new Progress<TransferProgress>(OnProgress);
        try
        {
            T result = await work(progress, cts.Token);
            return (OperationOutcome.Succeeded, result);
        }
        catch (Exception ex) when (Format.IsCancellation(ex))
        {
            if (!linkLost.Task.IsCompleted)
            {
                _notifications.Post("Cancelled", $"{title} was cancelled.", InfoBarSeverity.Warning);
            }
            return (OperationOutcome.Cancelled, default);
        }
        catch (Exception ex)
        {
            LastError = ex;
            _acceptProgress = false; // late reports mustn't overwrite the failure state
            // A disconnect error usually arrives just before the shell hears that the link is gone;
            // give it a moment so the user gets one "Connection lost" message, not that plus a dialog.
            bool lost = linkLost.Task.IsCompleted
                || (ex is CalcLinkException c && CalcErrorCodes.IsDisconnect(c.Code)
                    && await Task.WhenAny(linkLost.Task, Task.Delay(LinkLossGrace)) == linkLost.Task);
            if (reportErrors && !lost)
            {
                // Stop the bar animating behind the error dialog.
                StatusText = $"{title} failed";
                IsIndeterminate = false;
                RateText = string.Empty;
                TimeLeftText = string.Empty;
                _taskbar.SetState(TaskbarProgressState.Error);
                _taskbar.SetValue(Math.Max(_estimator.Fraction ?? 1, 0.1)); // an empty red bar would be invisible
                await _dialogs.ShowErrorAsync($"{title} failed", ex);
            }
            return (OperationOutcome.Failed, default);
        }
        finally
        {
            _cts = null;
            _linkLost = null;
            _clock.Stop();
            _taskbar.Clear();
            IsBusy = false;
            Title = string.Empty;
            StatusText = string.Empty;
            RateText = string.Empty;
            ItemText = string.Empty;
            TimeLeftText = string.Empty;
        }
    }

    /// <summary>The exception of the most recent failed operation (useful with reportErrors: false).</summary>
    public Exception? LastError { get; private set; }

    private void OnProgress(TransferProgress p)
    {
        if (!IsBusy || !_acceptProgress || _cts is not { IsCancellationRequested: false })
        {
            return; // keep "Cancelling…" once Cancel was pressed
        }
        string status = ProgressEstimator.ParseItem(p.Status, out int index, out int count);
        if (!string.IsNullOrEmpty(status))
        {
            StatusText = status;
        }

        _estimator.Update(p.Fraction, index, count, _clock.Elapsed.TotalSeconds);
        ItemText = _estimator.ItemCount > 1 ? $"Item {_estimator.ItemIndex} of {_estimator.ItemCount}" : string.Empty;
        if (_estimator.Fraction is { } f)
        {
            IsIndeterminate = false;
            ProgressValue = f * 100;
            _taskbar.SetValue(f);
        }
        else
        {
            IsIndeterminate = true;
            _taskbar.SetState(TaskbarProgressState.Indeterminate);
        }
        TimeLeftText = _estimator.Remaining is { } left ? Format.TimeLeft(left) : string.Empty;
        RateText = Format.Rate(p.RateKbps);
    }
}
