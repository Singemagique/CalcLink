using Microsoft.UI.Dispatching;

namespace CalcLink.App.Helpers;

public static class DispatcherQueueExtensions
{
    /// <summary>Runs <paramref name="work"/> on the queue's thread (inline if already there) and awaits it.</summary>
    public static Task<T> EnqueueAsync<T>(this DispatcherQueue queue, Func<Task<T>> work)
    {
        if (queue.HasThreadAccess)
        {
            return work();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = queue.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!queued)
        {
            tcs.SetException(new InvalidOperationException("The UI thread is shutting down."));
        }
        return tcs.Task;
    }
}
