using CalcLink.App.Helpers;
using CalcLink.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalcLink.App.Services;

/// <summary>
/// Holds the backend service and the (single) open session. UI-thread affine: all members are
/// called and all change notifications are raised on the UI thread.
/// </summary>
public sealed partial class ConnectionService(WindowService window, SettingsService settings) : ObservableObject
{
    public ICalculatorService? Service { get; private set; }

    /// <summary>Why the backend couldn't be created (null when <see cref="Service"/> is available).</summary>
    public string? ServiceError { get; private set; }

    public string BackendName => Service?.BackendName ?? "Unavailable";

    /// <summary>True when the simulator backend is in use.</summary>
    public bool IsSimulator { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(ModelName), nameof(DeviceName), nameof(StatusText))]
    public partial ICalculatorSession? Session { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelName), nameof(StatusText))]
    public partial CalcInfo? Info { get; private set; }

    public bool IsConnected => Session is not null;

    public string ModelName => Info is { ProductName.Length: > 0 } info
        ? info.ProductName
        : Session is { } s ? ModelNames.Of(s.Model) : string.Empty;

    public string DeviceName => Session?.Device.DisplayName ?? string.Empty;

    public string StatusText => Session is null
        ? "Not connected"
        : string.Equals(DeviceName, ModelName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(DeviceName)
            ? $"Connected to {ModelName}"
            : $"Connected to {ModelName} — {DeviceName}";

    /// <summary>Raised when the session opens, closes or is lost.</summary>
    public event EventHandler? SessionChanged;

    /// <summary>Raised (on the UI thread) when the link is lost unexpectedly.</summary>
    public event EventHandler<CalcLinkException>? ConnectionLost;

    public void InitializeBackend(CalculatorBackend backend)
    {
        try
        {
            Service = CalculatorServiceFactory.Create(backend);
            ServiceError = null;
            IsSimulator = Service.IsSimulator;
            SimulatorReason = IsSimulator && backend != CalculatorBackend.Simulator
                ? SafeNativeReason()
                : null;
        }
        catch (Exception ex)
        {
            Service = null;
            ServiceError = ex is NotImplementedException
                ? "The calculator backend is not available in this build yet (CalculatorServiceFactory is not implemented)."
                : $"The calculator backend could not be started: {Format.Error(ex)}";
            if (backend == CalculatorBackend.Native)
            {
                ServiceError += " You can switch to the simulator in Settings.";
            }
        }
        OnPropertyChanged(nameof(Service));
        OnPropertyChanged(nameof(ServiceError));
        OnPropertyChanged(nameof(BackendName));
        OnPropertyChanged(nameof(IsSimulator));
        OnPropertyChanged(nameof(SimulatorReason));
    }

    /// <summary>Why Auto fell back to the simulator (null if the simulator was chosen explicitly).</summary>
    public string? SimulatorReason { get; private set; }

    private static string? SafeNativeReason()
    {
        try
        {
            return CalculatorServiceFactory.NativeUnavailableReason is { Length: > 0 } reason
                ? $"Native backend unavailable: {reason.TrimEnd().TrimEnd('.')}."
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool Supports(CalcFeatures feature) => Session?.Supports(feature) ?? false;

    /// <summary>Tooltip text for a feature-gated command.</summary>
    public string FeatureTip(CalcFeatures feature, string label) =>
        Session is null ? $"{label} (not connected)"
        : Supports(feature) ? label
        : $"Not supported on {ModelName}";

    public Task<IReadOnlyList<DeviceDescriptor>> ProbeAsync(CancellationToken ct) =>
        Service is { } service
            ? service.ProbeAsync(ct)
            : throw new InvalidOperationException(ServiceError ?? "No calculator backend.");

    public async Task ConnectAsync(DeviceDescriptor device, CancellationToken ct)
    {
        if (Service is null)
        {
            throw new InvalidOperationException(ServiceError ?? "No calculator backend.");
        }
        await DisconnectAsync();

        var options = new ConnectOptions(TimeoutSeconds: settings.Current.LinkTimeoutSeconds);
        var session = await Service.ConnectAsync(device, options, ct);
        session.Disconnected += OnSessionDisconnected;
        try
        {
            Info = null;
            Session = session;
            await RefreshInfoAsync(ct);
        }
        catch
        {
            // Cancelled (or failed) after the link opened, while reading the calculator's info: the UI
            // reports the connect as cancelled, so don't keep a half-set-up session holding the cable (B7).
            session.Disconnected -= OnSessionDisconnected;
            if (ReferenceEquals(Session, session))
            {
                Session = null;
                Info = null;
            }
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception)
            {
                // The link may already be gone.
            }
            throw;
        }
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads CalcInfo; failures are non-fatal (the info card just stays empty).</summary>
    public async Task RefreshInfoAsync(CancellationToken ct = default)
    {
        if (Session is not { } session)
        {
            return;
        }
        try
        {
            Info = await session.GetInfoAsync(ct);
        }
        catch (Exception ex) when (ex is CalcLinkException or NotSupportedException or NotImplementedException)
        {
            Info = null;
        }
    }

    public async Task DisconnectAsync()
    {
        if (Session is not { } session)
        {
            return;
        }
        session.Disconnected -= OnSessionDisconnected;
        Session = null;
        Info = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // The link may already be gone; nothing useful to report.
        }
    }

    private void OnSessionDisconnected(object? sender, CalcLinkException e)
    {
        // Raised on a backend worker thread: marshal to the UI thread.
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(sender, Session))
            {
                return;
            }
            await DisconnectAsync();
            ConnectionLost?.Invoke(this, e);
        });
    }
}
