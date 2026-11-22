namespace CalcLink.Core.Simulation;

/// <summary>The fake calculators the simulator can offer.</summary>
public enum SimulatedModel
{
    /// <summary>TI-84 Plus CE Python Edition (CalcModel.TI84PCE_USB over DirectLink).</summary>
    TI84PlusCEPython,
    /// <summary>TI-Nspire CX II CAS (CalcModel.Nspire), with folders.</summary>
    NspireCXII,
    /// <summary>TI-89 Titanium (CalcModel.TI89T_USB), with folders.</summary>
    TI89Titanium,
}

/// <summary>Tuning knobs for <see cref="SimulatedCalculatorService"/>.</summary>
public sealed record SimulatorOptions
{
    /// <summary>Latency of a simple command (ready check, dirlist, delete...).</summary>
    public TimeSpan CommandDelay { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>Simulated link throughput for transfers (bytes/s). 0 or less = instantaneous.</summary>
    public double BytesPerSecond { get; init; } = 24_000;

    /// <summary>Upper bound on the simulated duration of a single transfer (so a 4 MB ROM dump isn't 3 minutes).</summary>
    public TimeSpan MaxTransferTime { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Time taken by a USB probe.</summary>
    public TimeSpan ProbeDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>After a (simulated) disconnect the device disappears from probes for this long.</summary>
    public TimeSpan ReplugDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Fault injection: the Nth operation on a session (1-based) fails as if the cable was pulled.</summary>
    public int? DisconnectAfterOperations { get; init; }

    /// <summary>Fault injection: probability (0..1) that any operation fails as if the cable was pulled.</summary>
    public double RandomDisconnectProbability { get; init; }

    /// <summary>Which fake calculators are "plugged in".</summary>
    public IReadOnlyList<SimulatedModel> Models { get; init; } =
        [SimulatedModel.TI84PlusCEPython, SimulatedModel.NspireCXII, SimulatedModel.TI89Titanium];

    /// <summary>Seed for fault injection randomness (null = time-based).</summary>
    public int? RandomSeed { get; init; }

    /// <summary>No artificial delays at all: for unit tests.</summary>
    public static SimulatorOptions Instant => new()
    {
        CommandDelay = TimeSpan.Zero,
        BytesPerSecond = 0,
        MaxTransferTime = TimeSpan.Zero,
        ProbeDelay = TimeSpan.Zero,
        ReplugDelay = TimeSpan.Zero,
    };
}
