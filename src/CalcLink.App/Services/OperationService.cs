using CalcLink.App.Helpers;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CalcLink.App.Services;

public enum OperationOutcome { Succeeded, Cancelled, Failed, Busy }

/// <summary>
/// Runs long link operations one at a time and drives the shell's transfer panel
/// (status, determinate/indeterminate progress, rate, Cancel). Errors are shown in a dialog,
/// cancellations as a "Cancelled" notification.
/// </summary>
public sealed partial class OperationService(DialogService dialogs, NotificationService notifications) : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; } = true;

    /// <summary>0..100.</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string RateText { get; private set; } = string.Empty;

    public event EventHandler? BusyChanged;

    partial void OnIsBusyChanged(bool value) => BusyChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        StatusText = "Cancelling…";
        _cts?.Cancel();
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
            notifications.Post("Busy", "Another operation is still running. Wait for it to finish or cancel it.",
                InfoBarSeverity.Warning);
            return (OperationOutcome.Busy, default);
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        StatusText = title + "…";
        IsIndeterminate = true;
        ProgressValue = 0;
        RateText = string.Empty;
        IsBusy = true;

        // Created on the UI thread, so reports are marshalled back to it.
        var progress = new Progress<TransferProgress>(OnProgress);
        try
        {
            T result = await work(progress, cts.Token);
            return (OperationOutcome.Succeeded, result);
        }
        catch (Exception ex) when (Format.IsCancellation(ex))
        {
            notifications.Post("Cancelled", $"{title} was cancelled.", InfoBarSeverity.Warning);
            return (OperationOutcome.Cancelled, default);
        }
        catch (Exception ex)
        {
            if (reportErrors)
            {
                await dialogs.ShowErrorAsync($"{title} failed", ex);
            }
            LastError = ex;
            return (OperationOutcome.Failed, default);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            StatusText = string.Empty;
            RateText = string.Empty;
        }
    }

    /// <summary>The exception of the most recent failed operation (useful with reportErrors: false).</summary>
    public Exception? LastError { get; private set; }

    private void OnProgress(TransferProgress p)
    {
        if (!IsBusy)
        {
            return;
        }
        if (!string.IsNullOrEmpty(p.Status))
        {
            StatusText = p.Status;
        }
        if (p.Fraction is { } f)
        {
            IsIndeterminate = false;
            ProgressValue = f * 100;
        }
        else
        {
            IsIndeterminate = true;
        }
        RateText = Format.Rate(p.RateKbps);
    }
}
