using System.Diagnostics;

namespace CalcLink.Core.Simulation;

/// <summary>
/// An in-memory backend with a few fake calculators, for UI development and tests without hardware.
/// Device state (variables, folders, clock) persists across reconnects for the lifetime of this object.
/// </summary>
public sealed class SimulatedCalculatorService : ICalculatorService
{
    private readonly List<SimDevice> _devices = [];
    private readonly Random _random;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public SimulatedCalculatorService(SimulatorOptions? options = null)
    {
        Options = options ?? new SimulatorOptions();
        _random = Options.RandomSeed is int seed ? new Random(seed) : new Random();
        int port = 1;
        foreach (SimulatedModel kind in Options.Models.Distinct())
            _devices.Add(SimDevice.Create(kind, port++));
    }

    public SimulatorOptions Options { get; }

    public string BackendName => "Simulator";

    /// <summary>All simulated devices, plugged in or not.</summary>
    public IReadOnlyList<DeviceDescriptor> AllDevices => _devices.Select(d => d.Descriptor).ToArray();

    internal double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    internal bool NextFault(double probability)
    {
        if (probability <= 0) return false;
        lock (_random) return _random.NextDouble() < probability;
    }

    internal SimDevice? FindDevice(DeviceDescriptor descriptor) => _devices.FirstOrDefault(d => d.Descriptor == descriptor);

    private static bool IsPlugged(SimDevice d) => Environment.TickCount64 >= d.UnpluggedUntilTicks;

    public async Task<IReadOnlyList<DeviceDescriptor>> ProbeAsync(CancellationToken ct = default)
    {
        await SimDelay.Wait(Options.ProbeDelay, ct).ConfigureAwait(false);
        var found = new List<DeviceDescriptor>();
        foreach (SimDevice d in _devices)
            lock (d.Gate)
                if (IsPlugged(d))
                    found.Add(d.Descriptor);
        return found;
    }

    public bool IsSimulator => true;

    public Task<ICalculatorSession> ConnectAsync(DeviceDescriptor device, CancellationToken ct = default)
        => ConnectAsync(device, ConnectOptions.Default, ct);

    /// <remarks>The simulator ignores <see cref="ConnectOptions.TimeoutSeconds"/>.</remarks>
    public async Task<ICalculatorSession> ConnectAsync(DeviceDescriptor device, ConnectOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        await SimDelay.Wait(Options.CommandDelay * 3, ct).ConfigureAwait(false);

        SimDevice d = FindDevice(device)
            ?? throw new CalcLinkException(CalcErrorCodes.CableNoCable, $"No calculator found: {device.DisplayName}.");
        lock (d.Gate)
        {
            if (!IsPlugged(d))
                throw new CalcLinkException(CalcErrorCodes.CableNoCable, $"{device.DisplayName} is not connected.");
            if (d.InUse)
                throw new CalcLinkException(CalcErrorCodes.CableBusy, $"{device.DisplayName} is already in use by another session.");
            d.InUse = true;
        }
        return new SimulatedCalculatorSession(this, d);
    }

    public TiFileClass ClassifyFile(string path, out CalcModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return SimFiles.Classify(path, out model);
    }
}

internal static class SimDelay
{
    public static Task Wait(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
    }
}
