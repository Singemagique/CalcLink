namespace CalcLink.Core.Native;

/// <summary>
/// The real-hardware backend over calclink_native.dll (tilibs). Discovery and file classification run on a
/// shared service-level worker thread; each session gets its own link thread.
/// </summary>
internal sealed unsafe partial class NativeCalculatorService : ICalculatorService, IDisposable
{
    private readonly SerialWorker _worker = new("CalcLink discovery");
    private readonly int _timeoutDs;

    /// <param name="timeoutDeciseconds">Link timeout in tenths of a second; 0 = tilibs default.</param>
    public NativeCalculatorService(int timeoutDeciseconds = 0)
    {
        NativeRuntime.EnsureInitialized();
        _timeoutDs = timeoutDeciseconds;
        string versions = _worker.Invoke(() => NativeMethods.FromUtf8(NativeMethods.cl_lib_versions()));
        BackendName = string.IsNullOrWhiteSpace(versions) ? "tilibs" : $"tilibs ({versions})";
    }

    public string BackendName { get; }

    public Task<IReadOnlyList<DeviceDescriptor>> ProbeAsync(CancellationToken ct = default)
        => _worker.RunAsync<IReadOnlyList<DeviceDescriptor>>(() =>
        {
            int capacity = 16;
            while (true)
            {
                var buffer = new ClDevice[capacity];
                int count = 0;
                int rc;
                fixed (ClDevice* p = buffer)
                    rc = NativeMethods.cl_probe_usb(p, capacity, &count);
                if (rc != CalcErrorCodes.Ok)
                    throw new CalcLinkException(rc, $"USB probe failed: {NativeMethods.ErrorMessage(rc)}");
                if (count > capacity && capacity < 1024)
                {
                    capacity = count;
                    continue;
                }

                var result = new List<DeviceDescriptor>(Math.Min(count, capacity));
                for (int i = 0; i < Math.Min(count, capacity); i++)
                {
                    ClDevice d = buffer[i];
                    string name = NativeMethods.FromFixedUtf8(d.name, 64);
                    if (name.Length == 0)
                        name = NativeMethods.FromUtf8(NativeMethods.cl_model_name(d.calc_model));
                    result.Add(new DeviceDescriptor(name, (CableModel)d.cable_model, d.cable_port,
                        (CalcModel)d.calc_model, d.vid, d.pid));
                }
                return result;
            }
        }, ct);

    public TiFileClass ClassifyFile(string path, out CalcModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        (int rc, int cls, int calc) = _worker.Invoke(() =>
        {
            int c, m;
            int r = NativeMethods.cl_classify_file(path, &c, &m);
            return (r, c, m);
        });
        if (rc != CalcErrorCodes.Ok)
        {
            model = CalcModel.None;
            return TiFileClass.Unknown;
        }
        model = Enum.IsDefined((CalcModel)calc) ? (CalcModel)calc : CalcModel.None;
        return Enum.IsDefined((TiFileClass)cls) ? (TiFileClass)cls : TiFileClass.Unknown;
    }

    public void Dispose() => _worker.Dispose();
}

internal sealed partial class NativeCalculatorService
{
    public bool IsSimulator => false;

    public async Task<ICalculatorSession> ConnectAsync(DeviceDescriptor device, ConnectOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        int timeoutDs = options.TimeoutSeconds > 0
            ? (int)Math.Clamp(Math.Round(options.TimeoutSeconds * 10), 1, 600)
            : _timeoutDs;
        return await NativeCalculatorSession.OpenAsync(device, timeoutDs, ct).ConfigureAwait(false);
    }

    public Task<ICalculatorSession> ConnectAsync(DeviceDescriptor device, CancellationToken ct = default)
        => ConnectAsync(device, ConnectOptions.Default, ct);
}
