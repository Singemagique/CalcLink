using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CalcLink.App;

/// <summary>
/// Entry point (replaces the XAML-generated Main). CalcLink runs once per user: only one process can
/// own the link cable, and Explorer starts CalcLink again for each file you open or send. A second
/// launch hands its activation (its command line) to the running CalcLink, brings that window to the
/// front, and exits.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "CalcLink.Main";

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance())
        {
            return 0;
        }
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// Registers this process as "the" CalcLink, or forwards to the one already registered. Returns true
    /// when this process should exit.
    /// </summary>
    private static bool RedirectToRunningInstance()
    {
        AppInstance main;
        try
        {
            main = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return false; // single-instancing unavailable: run normally
        }
        if (main.IsCurrent)
        {
            main.Activated += (_, args) => App.OnRedirectedActivation(args);
            return false;
        }

        // Forward the activation and wait for it on a worker thread: RedirectActivationToAsync must not
        // block the STA. CoWaitForMultipleObjects keeps pumping COM while waiting.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset);
        _ = Task.Run(() =>
        {
            try
            {
                main.RedirectActivationToAsync(activation).AsTask().Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // The running instance went away; nothing more to do.
            }
            finally
            {
                done.Set();
            }
        });
        _ = CoWaitForMultipleObjects(0, 15_000, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);

        try
        {
            // We are the foreground process (the user just opened a file), so we may hand focus over.
            using var process = Process.GetProcessById((int)main.ProcessId);
            if (process.MainWindowHandle != 0)
            {
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // It exited meanwhile.
        }
        return true;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, [In] nint[] handles, out uint index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
