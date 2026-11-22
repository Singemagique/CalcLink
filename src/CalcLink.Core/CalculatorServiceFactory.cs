using CalcLink.Core.Native;
using CalcLink.Core.Simulation;

namespace CalcLink.Core;

public enum CalculatorBackend
{
    /// <summary>Use the native tilibs backend if calclink_native.dll loads, otherwise the simulator.</summary>
    Auto,
    /// <summary>Real hardware via calclink_native.dll (tilibs).</summary>
    Native,
    /// <summary>Simulated calculators, for UI development and tests without hardware.</summary>
    Simulator,
}

/// <summary>Creates the <see cref="ICalculatorService"/> the app uses. Contract shared with CalcLink.App.</summary>
public static class CalculatorServiceFactory
{
    /// <summary>True if the native backend (calclink_native.dll) can be loaded and its ABI version matches.</summary>
    /// <remarks>
    /// calclink_native.dll is looked for at the CALCLINK_NATIVE_PATH environment variable (a file or a
    /// folder) if set, then in <c>&lt;appdir&gt;\native\&lt;rid&gt;\</c>, <c>&lt;appdir&gt;\</c> and
    /// <c>&lt;appdir&gt;\runtimes\&lt;rid&gt;\native\</c>. A module that lacks the cl_abi_version export is
    /// unloaded and skipped. The result is cached; this never throws.
    /// </remarks>
    public static bool IsNativeAvailable => NativeRuntime.IsAvailable;

    /// <summary>Why <see cref="IsNativeAvailable"/> is false (for logs/about box), else null.</summary>
    public static string? NativeUnavailableReason => IsNativeAvailable ? null : NativeRuntime.UnavailableReason;

    public static ICalculatorService Create(CalculatorBackend backend = CalculatorBackend.Auto)
        => Create(backend, () => NativeRuntime.IsAvailable, static () => new NativeCalculatorService(), static () => new SimulatedCalculatorService());

    /// <summary>Testable core of <see cref="Create(CalculatorBackend)"/>.</summary>
    internal static ICalculatorService Create(CalculatorBackend backend, Func<bool> nativeAvailable,
        Func<ICalculatorService> createNative, Func<ICalculatorService> createSimulator)
    {
        switch (backend)
        {
            case CalculatorBackend.Simulator:
                return createSimulator();
            case CalculatorBackend.Native:
                return createNative(); // throws DllNotFoundException / CalcLinkException if unusable
            case CalculatorBackend.Auto:
                if (nativeAvailable())
                {
                    try
                    {
                        return createNative();
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                                   or BadImageFormatException or CalcLinkException)
                    {
                        // calclink_native.dll loads but tilibs failed to initialize: fall back to the simulator.
                    }
                }
                return createSimulator();
            default:
                throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
        }
    }
}
