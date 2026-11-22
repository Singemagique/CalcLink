using System.Collections.Concurrent;

namespace CalcLink.Core.Native;

/// <summary>
/// A dedicated background thread that runs queued work items one at a time, in order.
/// tilibs handles are not thread-safe, so every native call for a handle goes through one of these.
/// </summary>
internal sealed class SerialWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public SerialWorker(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    private void Loop()
    {
        foreach (Action item in _queue.GetConsumingEnumerable())
            item(); // items never throw (see RunAsync)
    }

    /// <summary>
    /// Queues <paramref name="work"/>. If <paramref name="ct"/> fires while the item is still queued the
    /// task is cancelled immediately and the work never runs. Once the work has started, the task
    /// completes only when the work returns (cancelling the native call is the work's own business).
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            tcs.SetCanceled(ct);
            return tcs.Task;
        }

        CancellationTokenRegistration queuedReg = ct.CanBeCanceled
            ? ct.Register(static s => ((TaskCompletionSource<T>)s!).TrySetCanceled(), tcs)
            : default;

        void Run()
        {
            queuedReg.Dispose(); // waits for a concurrently running callback
            if (tcs.Task.IsCompleted)
                return; // cancelled while queued
            try
            {
                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken.IsCancellationRequested ? oce.CancellationToken : ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        try
        {
            _queue.Add(Run);
        }
        catch (InvalidOperationException)
        {
            queuedReg.Dispose();
            throw new ObjectDisposedException(_thread.Name, "The calculator link has been closed.");
        }
        return tcs.Task;
    }

    public Task RunAsync(Action work, CancellationToken ct = default)
        => RunAsync<bool>(() => { work(); return true; }, ct);

    /// <summary>Runs synchronously on the worker (inline if already on it).</summary>
    public T Invoke<T>(Func<T> work)
        => IsCurrentThread ? work() : RunAsync(work).GetAwaiter().GetResult();

    /// <summary>Stops accepting work; already-queued items still run, then the thread exits.</summary>
    public void Complete()
    {
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Complete();
        if (!IsCurrentThread)
            _thread.Join(TimeSpan.FromSeconds(5));
    }
}
