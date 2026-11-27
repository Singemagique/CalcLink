using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CalcLink.Core.Native;

/// <summary>
/// <see cref="CalcVariable.BackendToken"/> for the native backend: an index into the session's current
/// cl_var array. The array lives until the next <see cref="ICalculatorSession.GetVariablesAsync"/> or
/// until the session is disposed; <see cref="Generation"/> detects tokens from an older list.
/// </summary>
internal sealed class NativeVarToken(NativeCalculatorSession owner, int generation, int index)
{
    public NativeCalculatorSession Owner { get; } = owner;
    public int Generation { get; } = generation;
    public int Index { get; } = index;
}

/// <summary>Per-session progress target, pinned with a GCHandle and passed to cl_set_progress as user data.</summary>
internal sealed class ProgressSink
{
    public IProgress<TransferProgress>? Target;
    public string Prefix = string.Empty;
}

/// <summary>
/// A tilibs link session. Threading: the cl_session handle is used ONLY on <see cref="_worker"/>,
/// a dedicated thread owned by this object; public methods queue work there and return Tasks.
/// The one exception is cl_cancel (thread-safe by contract), which is called from cancellation
/// callbacks under <see cref="_handleLock"/> so it can never race with cl_close.
/// </summary>
internal sealed unsafe partial class NativeCalculatorSession : ICalculatorSession
{
    private readonly SerialWorker _worker;
    private readonly Lock _handleLock = new();
    private readonly ProgressSink _sink = new();
    private GCHandle _sinkHandle;
    private nint _handle;          // written on the worker under _handleLock

    // Current dirlist (worker thread only).
    private ClVar* _list;
    private int _count;
    private int _generation;

    private int _disposed;
    private int _disconnectRaised;

    private NativeCalculatorSession(DeviceDescriptor device)
    {
        Device = device;
        Model = device.Model;
        _worker = new SerialWorker($"CalcLink link: {device.DisplayName}");
    }

    public DeviceDescriptor Device { get; }
    public CalcModel Model { get; private set; }
    public CalcFeatures Features { get; private set; }
    public bool CanLeaveExamMode { get; private set; }

    public event EventHandler<CalcLinkException>? Disconnected;

    private void OpenOnWorker(int timeoutDs)
    {
        nint h;
        if (Device.Model == CalcModel.None)
            throw new CalcLinkException(CalcErrorCodes.InvalidArgument,
                $"{Device.DisplayName}: the calculator didn't say which model it is. Choose the model to connect.");
        int rc = NativeMethods.cl_open_device((int)Device.Cable, Device.Port, Device.SerialPort, (int)Device.Model, timeoutDs, &h);
        if (rc != CalcErrorCodes.Ok)
            throw new CalcLinkException(rc, $"Could not open {Device.DisplayName}: {NativeMethods.ErrorMessage(rc)}");

        lock (_handleLock)
            _handle = h;

        Features = (CalcFeatures)NativeMethods.cl_features(h);
        CanLeaveExamMode = NativeMethods.cl_can_leave_exam_mode(h) != 0;
        int model = NativeMethods.cl_calc_model(h);
        if (model > 0)
            Model = (CalcModel)model;

        _sinkHandle = GCHandle.Alloc(_sink);
        NativeMethods.cl_set_progress(h, &OnProgress, (void*)GCHandle.ToIntPtr(_sinkHandle));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProgress(void* user, int current, int total, float rateKbps, byte* text)
    {
        // Never let an exception unwind into native code.
        try
        {
            if (user == null)
                return;
            if (GCHandle.FromIntPtr((nint)user).Target is not ProgressSink { Target: { } target } sink)
                return;
            string status = NativeMethods.FromUtf8(text);
            if (sink.Prefix.Length > 0)
                status = status.Length > 0 ? $"{sink.Prefix}: {status}" : sink.Prefix;
            target.Report(new TransferProgress(current, total, rateKbps, status));
        }
        catch
        {
            // ignored
        }
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Queues <paramref name="work"/> on the link thread. Feature-gating and "disposed" failures are
    /// returned as faulted tasks, before anything is queued or any native code runs.
    /// </summary>
    private Task<T> Run<T>(CalcFeatures required, Func<T> work, CancellationToken ct, IProgress<TransferProgress>? progress = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(NativeCalculatorSession), "The calculator link has been closed."));
        if ((Features & required) != required)
            return Task.FromException<T>(new FeatureNotSupportedException(required, Model));
        return _worker.RunAsync(() =>
        {
            if (_handle == 0)
                throw new CalcLinkException(CalcErrorCodes.NotOpen, "The calculator link is closed.");
            _sink.Target = progress;
            _sink.Prefix = string.Empty;
            // One operation = one work item, possibly several native calls (e.g. one per file). Clear
            // the native cancel flag once here, BEFORE listening for cancellation: from then on a
            // cancel stays set across the calls instead of being wiped by the next one (B6).
            NativeMethods.cl_reset_cancel(_handle);
            // Only cancel the native call while THIS item is running, so a token belonging to a queued
            // operation can't abort somebody else's transfer.
            using CancellationTokenRegistration reg = ct.CanBeCanceled
                ? ct.UnsafeRegister(static s => ((NativeCalculatorSession)s!).CancelNative(), this)
                : default;
            try
            {
                return work();
            }
            finally
            {
                _sink.Target = null;
                _sink.Prefix = string.Empty;
            }
        }, ct);
    }

    private Task<T> Run<T>(Func<T> work, CancellationToken ct, IProgress<TransferProgress>? progress = null)
        => Run(CalcFeatures.None, work, ct, progress);

    private Task Run(CalcFeatures required, Action work, CancellationToken ct, IProgress<TransferProgress>? progress = null)
        => Run(required, () => { work(); return true; }, ct, progress);

    private Task Run(Action work, CancellationToken ct, IProgress<TransferProgress>? progress = null)
        => Run(CalcFeatures.None, work, ct, progress);

    private void CancelNative()
    {
        lock (_handleLock)
        {
            if (_handle != 0)
                NativeMethods.cl_cancel(_handle);
        }
    }

    /// <summary>Throws the right exception for a nonzero native return code (worker thread).</summary>
    private void Check(int rc, CancellationToken ct, string? what = null)
    {
        if (rc == CalcErrorCodes.Ok)
            return;
        if (rc is CalcErrorCodes.Cancelled or CalcErrorCodes.CalcAbort || ct.IsCancellationRequested)
            throw new OperationCanceledException("The operation was cancelled.", ct);

        string message = NativeMethods.ErrorMessage(rc);
        if (what is not null)
            message = $"{what}: {message}";
        var ex = new CalcLinkException(rc, message);
        if (CalcErrorCodes.IsDisconnect(rc))
            RaiseDisconnected(ex);
        throw ex;
    }

    private void RaiseDisconnected(CalcLinkException ex)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
            return;
        // Raise off the link thread so a handler that awaits DisposeAsync can't deadlock it.
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var (self, error) = state;
            try { self.Disconnected?.Invoke(self, error); } catch { /* handler bugs must not kill the pool */ }
        }, (this, ex), preferLocal: false);
    }

    private void Require(CalcFeatures feature)
    {
        if ((Features & feature) != feature)
            throw new FeatureNotSupportedException(feature, Model);
    }

    /// <summary>Resolves a CalcVariable from GetVariablesAsync to its native cl_var (worker thread).</summary>
    private ClVar* Resolve(CalcVariable variable)
    {
        if (variable.BackendToken is not NativeVarToken token || token.Owner != this)
            throw new CalcLinkException(CalcErrorCodes.InvalidArgument,
                $"'{variable.FullName}' was not listed by this connection.");
        if (token.Generation != _generation || _list == null || (uint)token.Index >= (uint)_count)
            throw new CalcLinkException(CalcErrorCodes.InvalidArgument,
                $"'{variable.FullName}' comes from an outdated variable list. Refresh the list and try again.");
        return _list + token.Index;
    }

    private void FreeList()
    {
        if (_list != null)
        {
            NativeMethods.cl_free_dirlist(_list, _count);
            _list = null;
        }
        _count = 0;
        _generation++; // invalidates every token handed out so far
    }

    // ------------------------------------------------------------------ ICalculatorSession

    public Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        return Run(CalcFeatures.IsReady, () =>
        {
            int rc = NativeMethods.cl_is_ready(_handle);
            if (rc is CalcErrorCodes.CalcNotReady or CalcErrorCodes.CableReadTimeout or CalcErrorCodes.CableWriteTimeout)
                return false;
            Check(rc, ct);
            return true;
        }, ct);
    }

    public Task<CalcInfo> GetInfoAsync(CancellationToken ct = default)
    {
        return Run(CalcFeatures.Version, () =>
        {
            ClCalcInfo info;
            Check(NativeMethods.cl_get_info(_handle, &info), ct);
            CalcInfo result = ToCalcInfo(&info, Model);
            string id = result.ProductId;
            if (id.Length == 0 && (Features & CalcFeatures.IdList) != 0)
            {
                id = ReadCalcId(ct);
            }
            result = result with { CalcId = id };
            // Models whose version reply has no memory figures (the TI-73/83+/84+ over DBUS) answer a
            // separate free-memory request; restore uses it to warn before sending more than fits.
            if (result.RamFree == 0 && result.FlashFree == 0 && (Features & CalcFeatures.MemFree) != 0)
            {
                var (ram, flash) = ReadMemFree(ct);
                result = result with { RamFree = ram, FlashFree = flash };
            }
            return result;
        }, ct);
    }

    // Like the ID-list: a failure only leaves the figures unknown, unless the link itself went away.
    private (ulong Ram, ulong Flash) ReadMemFree(CancellationToken ct)
    {
        ulong ram = 0, flash = 0;
        int rc = NativeMethods.cl_get_memfree(_handle, &ram, &flash);
        if (rc == CalcErrorCodes.Ok)
            return (ram, flash);
        if (rc is CalcErrorCodes.Cancelled or CalcErrorCodes.CalcAbort || ct.IsCancellationRequested
            || CalcErrorCodes.IsDisconnect(rc))
            Check(rc, ct, "Reading free memory");
        return (0, 0);
    }

    // The ID-list is a separate exchange. If it fails (and the link is still up), the rest of the
    // info is still good, so report the ID as unknown rather than failing the whole refresh.
    private string ReadCalcId(CancellationToken ct)
    {
        const int Capacity = 64;
        byte* buffer = stackalloc byte[Capacity];
        int rc = NativeMethods.cl_get_calc_id(_handle, buffer, Capacity);
        if (rc == CalcErrorCodes.Ok)
            return NativeMethods.FromFixedUtf8(buffer, Capacity);
        if (rc is CalcErrorCodes.Cancelled or CalcErrorCodes.CalcAbort || ct.IsCancellationRequested
            || CalcErrorCodes.IsDisconnect(rc))
            Check(rc, ct, "Reading the calculator ID");
        return string.Empty;
    }

    internal static CalcInfo ToCalcInfo(ClCalcInfo* i, CalcModel fallbackModel)
    {
        var m = (InfosMask)i->mask;
        bool Has(InfosMask bit) => (m & bit) != 0;
        bool? Flag(InfosMask bit, byte value) => Has(bit) ? value != 0 : null;

        CalcModel model = Has(InfosMask.CalcModel) && i->calc_model > 0 ? (CalcModel)i->calc_model : fallbackModel;
        string productName = NativeMethods.FromFixedUtf8(i->product_name, 64);
        if (productName.Length == 0)
            productName = NativeMethods.FromUtf8(NativeMethods.cl_model_name((int)model));
        string boot = NativeMethods.FromFixedUtf8(i->boot_version, 32);
        if (boot.Length == 0)
            boot = NativeMethods.FromFixedUtf8(i->boot2_version, 32);

        return new CalcInfo(
            model,
            productName,
            NativeMethods.FromFixedUtf8(i->product_id, 32),
            NativeMethods.FromFixedUtf8(i->os_version, 32),
            boot,
            i->ram_user, i->ram_free, i->flash_user, i->flash_free,
            i->lcd_width, i->lcd_height, i->bits_per_pixel,
            BatteryOk: Flag(InfosMask.BatteryEnough, i->battery_ok),
            ColorScreen: Flag(InfosMask.ColorScreen, i->color_screen),
            PythonOnBoard: Flag(InfosMask.PythonOnBoard, i->python_on_board),
            ExactMath: Flag(InfosMask.ExactMath, i->exact_math),
            ClockSupport: Flag(InfosMask.ClockSupport, i->clock_support),
            RunLevel: Has(InfosMask.RunLevel) ? i->run_level : 0);
    }

    public Task<IReadOnlyList<CalcVariable>> GetVariablesAsync(CancellationToken ct = default)
    {
        return Run<IReadOnlyList<CalcVariable>>(CalcFeatures.DirList, () =>
        {
            FreeList();
            ClVar* list = null;
            int count = 0;
            int rc = NativeMethods.cl_get_dirlist(_handle, &list, &count);
            if (rc != CalcErrorCodes.Ok)
            {
                if (list != null)
                    NativeMethods.cl_free_dirlist(list, count);
                Check(rc, ct);
            }
            _list = list;
            _count = list == null ? 0 : count;

            var result = new CalcVariable[_count];
            for (int i = 0; i < _count; i++)
            {
                result[i] = ToCalcVariable(_list + i, new NativeVarToken(this, _generation, i));
            }
            return result;
        }, ct);
    }

    private static CalcVariable ToCalcVariable(ClVar* v, object? token) => new()
    {
        Folder = NativeMethods.FromUtf8(v->folder),
        Name = NativeMethods.FromUtf8(v->name),
        TypeName = NativeMethods.FromUtf8(v->type_name),
        FileExtension = NativeMethods.FromUtf8(v->file_ext),
        Size = v->size,
        RawType = v->type,
        Attribute = v->attr <= (byte)VariableAttribute.Archived ? (VariableAttribute)v->attr : VariableAttribute.None,
        IsApp = v->is_app != 0,
        BackendToken = token,
    };

    // Stateless and link-free (reads the file only), so it runs on the caller's thread.
    public IReadOnlyList<CalcVariable> GetFileVariables(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ClVar* list = null;
        int count = 0;
        try
        {
            if (NativeMethods.cl_read_file_vars(path, &list, &count) != CalcErrorCodes.Ok || list == null)
                return [];
            var result = new CalcVariable[count];
            for (int i = 0; i < count; i++)
                result[i] = ToCalcVariable(list + i, null);
            return result;
        }
        finally
        {
            if (list != null)
                NativeMethods.cl_free_dirlist(list, count);
        }
    }

    public Task SendFilesAsync(IReadOnlyList<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Run(() =>
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string path = paths[i];
                int cls, model;
                int rc = NativeMethods.cl_classify_file(path, &cls, &model);
                if (rc != CalcErrorCodes.Ok)
                    Check(rc, ct, Path.GetFileName(path));
                Require(RequiredFeatureFor((TiFileClass)cls, path));

                _sink.Prefix = paths.Count > 1 ? $"{Path.GetFileName(path)} ({i + 1}/{paths.Count})" : Path.GetFileName(path);
                Check(NativeMethods.cl_send_file(_handle, path), ct, Path.GetFileName(path));
            }
        }, ct, progress);
    }

    internal static CalcFeatures RequiredFeatureFor(TiFileClass cls, string path) => cls switch
    {
        TiFileClass.Variable or TiFileClass.TiGroup => CalcFeatures.Vars,
        TiFileClass.App or TiFileClass.Certificate => CalcFeatures.Flash,
        TiFileClass.Os => CalcFeatures.Os,
        TiFileClass.Backup => CalcFeatures.Backup,
        _ => throw new CalcLinkException(CalcErrorCodes.BadFile, $"'{Path.GetFileName(path)}' is not a TI calculator file."),
    };

    public Task<IReadOnlyList<string>> ReceiveAsync(IReadOnlyList<CalcVariable> variables, string destinationFolder,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentException.ThrowIfNullOrEmpty(destinationFolder);
        CalcFeatures required = CalcFeatures.None;
        foreach (CalcVariable v in variables)
            required |= v.IsApp ? CalcFeatures.Flash : CalcFeatures.Vars;

        return Run<IReadOnlyList<string>>(required, () =>
        {
            Directory.CreateDirectory(destinationFolder);
            var taken = CalcFileNames.NewPathSet();
            var written = new List<string>(variables.Count);
            for (int i = 0; i < variables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                CalcVariable variable = variables[i];
                ClVar* v = Resolve(variable);
                string path = CalcFileNames.MakeUnique(destinationFolder,
                    CalcFileNames.ForVariable(variable.Name, variable.FileExtension), taken);

                _sink.Prefix = variables.Count > 1 ? $"{variable.FullName} ({i + 1}/{variables.Count})" : variable.FullName;
                int rc = NativeMethods.cl_recv_var(_handle, v, path);
                if (rc != CalcErrorCodes.Ok)
                {
                    TryDelete(path); // don't leave a truncated file behind
                    Check(rc, ct, variable.FullName);
                }
                written.Add(path);
            }
            return written;
        }, ct, progress);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public Task DeleteAsync(IReadOnlyList<CalcVariable> variables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return Run(CalcFeatures.DeleteVar, () =>
        {
            foreach (CalcVariable variable in variables)
            {
                ct.ThrowIfCancellationRequested();
                Check(NativeMethods.cl_delete_var(_handle, Resolve(variable)), ct, variable.FullName);
            }
        }, ct);
    }

    public Task RenameAsync(CalcVariable variable, string newFolder, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        return Run(CalcFeatures.Rename, () => Check(NativeMethods.cl_rename_var(_handle, Resolve(variable), newFolder ?? string.Empty, newName),
            ct, variable.FullName), ct);
    }

    public Task SetAttributeAsync(CalcVariable variable, VariableAttribute attribute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return Run(CalcFeatures.ChangeAttr, () => Check(NativeMethods.cl_set_attr(_handle, Resolve(variable), (int)attribute), ct, variable.FullName), ct);
    }

    public Task CreateFolderAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Run(CalcFeatures.NewFolder, () => Check(NativeMethods.cl_new_folder(_handle, name), ct, name), ct);
    }

    /// <summary>No tilibs feature bit covers execute; the native side reports unsupported models.</summary>
    public Task ExecuteAsync(CalcVariable variable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return Run(() => Check(NativeMethods.cl_execute(_handle, Resolve(variable)), ct, variable.FullName), ct);
    }

    public Task SendKeyAsync(uint keyCode, CancellationToken ct = default)
    {
        return Run(CalcFeatures.Keys, () => Check(NativeMethods.cl_send_key(_handle, keyCode), ct), ct);
    }

    public Task<ScreenImage> CaptureScreenAsync(CancellationToken ct = default)
    {
        return Run(CalcFeatures.Screen, () =>
        {
            byte* pixels = null;
            int width = 0, height = 0;
            int rc = NativeMethods.cl_recv_screen(_handle, &pixels, &width, &height);
            try
            {
                Check(rc, ct);
                if (pixels == null || width <= 0 || height <= 0 || width > 4096 || height > 4096)
                    throw new CalcLinkException(CalcErrorCodes.Internal, "The calculator returned an invalid screenshot.");
                byte[] bgra = new ReadOnlySpan<byte>(pixels, checked(width * height * 4)).ToArray();
                return new ScreenImage(width, height, bgra);
            }
            finally
            {
                if (pixels != null)
                    NativeMethods.cl_free(pixels);
            }
        }, ct);
    }

    public Task<CalcClock> GetClockAsync(CancellationToken ct = default)
    {
        return Run(CalcFeatures.Clock, () =>
        {
            ClClock c;
            Check(NativeMethods.cl_get_clock(_handle, &c), ct);
            DateTime value;
            try
            {
                value = new DateTime(c.year, c.month, c.day, c.hours, c.minutes, c.seconds, DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new CalcLinkException(CalcErrorCodes.Internal,
                    $"The calculator returned an invalid date/time ({c.year:D4}-{c.month:D2}-{c.day:D2} {c.hours:D2}:{c.minutes:D2}:{c.seconds:D2}).");
            }
            return new CalcClock(value, c.time_format, c.date_format);
        }, ct);
    }

    public Task SetClockAsync(CalcClock clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Run(CalcFeatures.Clock, () =>
        {
            DateTime t = clock.Value;
            ClClock c = default;
            c.year = (ushort)t.Year;
            c.month = (byte)t.Month;
            c.day = (byte)t.Day;
            c.hours = (byte)t.Hour;
            c.minutes = (byte)t.Minute;
            c.seconds = (byte)t.Second;
            // 0 = keep the calculator's current setting (anything else unknown is treated as "keep" too).
            c.time_format = (byte)(clock.TimeFormat is 12 or 24 ? clock.TimeFormat : 0);
            c.date_format = (byte)(clock.DateFormat is > 0 and <= byte.MaxValue ? clock.DateFormat : 0);
            Check(NativeMethods.cl_set_clock(_handle, &c), ct);
        }, ct);
    }

    public Task BackupAsync(string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Run(CalcFeatures.Backup,
            () => WriteViaTempFile(path, temp => NativeMethods.cl_backup(_handle, temp), rc => Check(rc, ct)),
            ct, progress);
    }

    public Task DumpRomAsync(string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Run(CalcFeatures.RomDump,
            () => WriteViaTempFile(path, temp => NativeMethods.cl_dump_rom(_handle, temp), rc => Check(rc, ct)),
            ct, progress);
    }

    public Task LeaveExamModeAsync(CancellationToken ct = default)
    {
        if (!CanLeaveExamMode)
            return Task.FromException(new CalcLinkException(CalcErrorCodes.NotSupported, $"Leaving exam mode isn't supported on {Model}."));
        return Run(() => Check(NativeMethods.cl_leave_exam_mode(_handle), ct, "Leaving exam mode"), ct);
    }

    /// <summary>
    /// Runs a native call that writes a file, so that <paramref name="path"/> is only replaced when it
    /// succeeds. The destination may be an existing file the user chose to overwrite (e.g. last month's
    /// backup), so a failed or cancelled transfer must never touch it. The temp file sits next to the
    /// destination (same volume, so the final move is atomic) and keeps its extension, because tilibs
    /// picks the backup format from the extension.
    /// </summary>
    /// <param name="path">Destination file.</param>
    /// <param name="write">Writes the given temp path; returns a calclink error code.</param>
    /// <param name="fail">Called with a non-zero code; must throw.</param>
    internal static void WriteViaTempFile(string path, Func<string, int> write, Action<int> fail)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory,
            $"{Path.GetFileNameWithoutExtension(full)}.{Guid.NewGuid():N}.partial{Path.GetExtension(full)}");
        try
        {
            int rc = write(temp);
            if (rc != CalcErrorCodes.Ok)
            {
                fail(rc);
                throw new InvalidOperationException($"Native call failed with code {rc}."); // fail() should have thrown
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // ------------------------------------------------------------------ lifetime

    private void CloseOnWorker()
    {
        FreeList();
        nint h;
        lock (_handleLock)
        {
            h = _handle;
            _handle = 0;
        }
        if (h != 0)
        {
            NativeMethods.cl_set_progress(h, null, null);
            NativeMethods.cl_close(h);
        }
        if (_sinkHandle.IsAllocated)
            _sinkHandle.Free();
    }
}

// Async methods cannot live in an unsafe context (CS4004), so they get their own partial part.
internal sealed partial class NativeCalculatorSession
{
    internal static async Task<NativeCalculatorSession> OpenAsync(DeviceDescriptor device, int timeoutDs, CancellationToken ct)
    {
        var session = new NativeCalculatorSession(device);
        try
        {
            await session._worker.RunAsync(() => session.OpenOnWorker(timeoutDs), ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session._disposed = 1;
            // If the open got as far as a native handle before failing, close it (and the progress
            // sink) on the worker before stopping it; otherwise the cable stays claimed.
            try
            {
                await session._worker.RunAsync(session.CloseOnWorker).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
            session._worker.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _worker.RunAsync(CloseOnWorker).ConfigureAwait(false);
        }
        finally
        {
            _worker.Complete(); // thread exits once the queue drains
        }
    }
}
