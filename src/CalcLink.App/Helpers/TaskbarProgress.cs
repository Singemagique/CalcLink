using System.Runtime.InteropServices;

namespace CalcLink.App.Helpers;

public enum TaskbarProgressState
{
    None = 0,          // TBPF_NOPROGRESS
    Indeterminate = 1, // TBPF_INDETERMINATE
    Normal = 2,        // TBPF_NORMAL
    Error = 4,         // TBPF_ERROR
    Paused = 8,        // TBPF_PAUSED
}

/// <summary>
/// Progress on the window's taskbar button (ITaskbarList3). Purely cosmetic: every failure is
/// swallowed. Call from the UI thread.
/// </summary>
public sealed class TaskbarProgress(Func<nint> hwnd)
{
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    private ITaskbarList3? _taskbar;
    private bool _failed;
    private TaskbarProgressState _state = TaskbarProgressState.None;
    private int _permille = -1;

    public void SetState(TaskbarProgressState state)
    {
        if (state == _state)
        {
            return;
        }
        if (Invoke((t, h) => t.SetProgressState(h, (int)state)))
        {
            _state = state;
            _permille = -1;
        }
    }

    /// <summary>Shows determinate progress (switches to Normal unless the state is Error/Paused).</summary>
    public void SetValue(double fraction)
    {
        if (_state is TaskbarProgressState.None or TaskbarProgressState.Indeterminate)
        {
            SetState(TaskbarProgressState.Normal);
        }
        int permille = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 1000);
        if (permille == _permille)
        {
            return;
        }
        if (Invoke((t, h) => t.SetProgressValue(h, (ulong)permille, 1000)))
        {
            _permille = permille;
        }
    }

    public void Clear() => SetState(TaskbarProgressState.None);

    private bool Invoke(Func<ITaskbarList3, nint, int> call)
    {
        nint h = hwnd();
        if (h == 0 || _failed)
        {
            return false;
        }
        try
        {
            if (_taskbar is null)
            {
                Type? type = Type.GetTypeFromCLSID(ClsidTaskbarList);
                if (type is null || Activator.CreateInstance(type) is not ITaskbarList3 taskbar)
                {
                    _failed = true;
                    return false;
                }
                taskbar.HrInit();
                _taskbar = taskbar;
            }
            return call(_taskbar, h) >= 0;
        }
        catch (Exception)
        {
            // No taskbar (e.g. Explorer not running) or COM unavailable: progress just isn't shown.
            _failed = true;
            return false;
        }
    }
}

// Only the methods up to SetProgressState are used, but the vtable order must be complete up to there
// (ITaskbarList, then ITaskbarList2, then ITaskbarList3).
[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(nint hwnd);
    [PreserveSig] int DeleteTab(nint hwnd);
    [PreserveSig] int ActivateTab(nint hwnd);
    [PreserveSig] int SetActiveAlt(nint hwnd);

    // ITaskbarList2
    [PreserveSig] int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

    // ITaskbarList3
    [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
    [PreserveSig] int SetProgressState(nint hwnd, int flags);
}
