// Shared contract between CalcLink.Core and CalcLink.App.
// The App only talks to these interfaces; backends (native tilibs, simulator) implement them.

namespace CalcLink.Core;

/// <summary>Entry point: discovers devices and opens sessions. Registered as a singleton.</summary>
public interface ICalculatorService
{
    /// <summary>Human-readable backend name, e.g. "tilibs 1.3.x" or "Simulator".</summary>
    string BackendName { get; }

    /// <summary>True for the simulated backend (no real hardware involved).</summary>
    bool IsSimulator { get; }

    /// <summary>Scans USB for connected calculators/cables.</summary>
    Task<IReadOnlyList<DeviceDescriptor>> ProbeAsync(CancellationToken ct = default);

    /// <summary>Opens a link session. Only one session should be open per device.</summary>
    Task<ICalculatorSession> ConnectAsync(DeviceDescriptor device, ConnectOptions options, CancellationToken ct = default);

    /// <summary>Opens a link session with <see cref="ConnectOptions.Default"/>.</summary>
    Task<ICalculatorSession> ConnectAsync(DeviceDescriptor device, CancellationToken ct = default)
        => ConnectAsync(device, ConnectOptions.Default, ct);

    /// <summary>Classifies a local file by extension and content (var, app, OS, backup...).</summary>
    TiFileClass ClassifyFile(string path, out CalcModel model);
}

/// <summary>
/// An open link to one calculator. All methods are safe to call from the UI thread:
/// implementations serialize work onto their own worker thread.
/// Cancelling a token aborts the current transfer (best effort; the link may need a reconnect).
/// </summary>
public interface ICalculatorSession : IAsyncDisposable
{
    DeviceDescriptor Device { get; }
    CalcModel Model { get; }
    CalcFeatures Features { get; }
    bool Supports(CalcFeatures feature) => (Features & feature) == feature;

    /// <summary>Raised once (on a thread-pool thread) when the link is lost, e.g. the cable was unplugged.
    /// The sender is always this session.</summary>
    event EventHandler<CalcLinkException>? Disconnected;

    Task<bool> IsReadyAsync(CancellationToken ct = default);
    Task<CalcInfo> GetInfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CalcVariable>> GetVariablesAsync(CancellationToken ct = default);

    /// <summary>
    /// The variables a variable or group file would create on this calculator, named as
    /// <see cref="GetVariablesAsync"/> names them, so callers can warn before overwriting. Reads only the
    /// local file (no link traffic). Empty for other file classes (apps, OS, backups...) or unreadable files.
    /// The returned items have no <see cref="CalcVariable.BackendToken"/>.
    /// </summary>
    IReadOnlyList<CalcVariable> GetFileVariables(string path);

    /// <summary>Sends files of any class (vars, groups, apps, OS, backups, TIGroups, certs) in order.</summary>
    Task SendFilesAsync(IReadOnlyList<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Receives variables into <paramref name="destinationFolder"/>; returns the written file paths.</summary>
    Task<IReadOnlyList<string>> ReceiveAsync(IReadOnlyList<CalcVariable> variables, string destinationFolder,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default);

    Task DeleteAsync(IReadOnlyList<CalcVariable> variables, CancellationToken ct = default);
    Task RenameAsync(CalcVariable variable, string newFolder, string newName, CancellationToken ct = default);
    Task SetAttributeAsync(CalcVariable variable, VariableAttribute attribute, CancellationToken ct = default);
    Task CreateFolderAsync(string name, CancellationToken ct = default);
    Task ExecuteAsync(CalcVariable variable, CancellationToken ct = default);

    Task SendKeyAsync(uint keyCode, CancellationToken ct = default);
    Task<ScreenImage> CaptureScreenAsync(CancellationToken ct = default);
    Task<CalcClock> GetClockAsync(CancellationToken ct = default);
    Task SetClockAsync(CalcClock clock, CancellationToken ct = default);

    /// <summary>Full backup. A ".tig" path produces a TIGroup, anything else a native backup.</summary>
    Task BackupAsync(string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);
    Task DumpRomAsync(string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);
}
