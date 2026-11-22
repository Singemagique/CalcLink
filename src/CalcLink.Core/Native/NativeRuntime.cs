using System.Reflection;
using System.Runtime.InteropServices;

namespace CalcLink.Core.Native;

/// <summary>
/// Locates and loads calclink_native.dll, checks its ABI version and initializes tilibs once per process.
/// </summary>
internal static class NativeRuntime
{
    /// <summary>Optional override: a full path to calclink_native.dll, or a directory containing it.</summary>
    public const string PathEnvironmentVariable = "CALCLINK_NATIVE_PATH";

    private static readonly Lock s_gate = new();
    private static bool s_resolverInstalled;
    private static bool? s_available;
    private static string? s_unavailableReason;
    private static bool s_initialized;
    private static string? s_loadedFrom;

    /// <summary>Where calclink_native.dll was loaded from, if our resolver found it.</summary>
    public static string? LoadedFrom => s_loadedFrom;

    /// <summary>Why the native backend is unavailable (after <see cref="IsAvailable"/> returned false).</summary>
    public static string? UnavailableReason => s_unavailableReason;

    /// <summary>
    /// True if calclink_native.dll loads and reports <see cref="NativeMethods.ExpectedAbiVersion"/>.
    /// Cached after the first call; never throws.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            lock (s_gate)
            {
                s_available ??= Probe(out s_unavailableReason);
                return s_available.Value;
            }
        }
    }

    /// <summary>Loads the library (if needed) and calls cl_init once. Throws if unavailable.</summary>
    public static void EnsureInitialized()
    {
        lock (s_gate)
        {
            if (s_initialized)
                return;
            s_available ??= Probe(out s_unavailableReason);
            if (!s_available.Value)
                throw new DllNotFoundException($"The native CalcLink backend is unavailable: {s_unavailableReason}");
            int rc = NativeMethods.cl_init();
            if (rc != CalcErrorCodes.Ok)
                throw new CalcLinkException(rc, $"cl_init failed: {NativeMethods.ErrorMessage(rc)}");
            s_initialized = true;
        }
    }

    private static bool Probe(out string? reason)
    {
        try
        {
            InstallResolver();
            int abi = NativeMethods.cl_abi_version();
            if (abi != NativeMethods.ExpectedAbiVersion)
            {
                reason = $"calclink_native.dll ABI version {abi}, expected {NativeMethods.ExpectedAbiVersion}.";
                return false;
            }
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or BadImageFormatException or MarshalDirectiveException
                                       or InvalidOperationException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static void InstallResolver()
    {
        if (s_resolverInstalled)
            return;
        s_resolverInstalled = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeRuntime).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already set for this assembly (e.g. by a host); fall back to default probing.
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeMethods.LibraryName, StringComparison.OrdinalIgnoreCase))
            return 0;

        foreach (string candidate in CandidatePaths(AppContext.BaseDirectory,
                     Environment.GetEnvironmentVariable(PathEnvironmentVariable),
                     RuntimeInformation.ProcessArchitecture))
        {
            if (!File.Exists(candidate))
                continue;
            // An absolute path makes Windows also search the DLL's own directory for its dependencies
            // (tilibs, glib, libusb), so they can live next to calclink_native.dll in native\<rid>.
            if (NativeLibrary.TryLoad(candidate, assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories,
                    out nint handle))
            {
                // Safeguard: never bind to some other module that happens to have the same name
                // (e.g. a managed assembly); it must export the calclink ABI.
                if (!NativeLibrary.TryGetExport(handle, "cl_abi_version", out _))
                {
                    NativeLibrary.Free(handle);
                    continue;
                }
                s_loadedFrom = candidate;
                return handle;
            }
        }
        return 0; // let the runtime try its default probing
    }

    /// <summary>The files we try, in order. Internal for tests.</summary>
    internal static IEnumerable<string> CandidatePaths(string baseDirectory, string? overridePath, Architecture arch)
    {
        const string file = NativeMethods.LibraryName + ".dll";
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return Directory.Exists(overridePath) ? Path.Combine(overridePath, file) : overridePath;
        }

        string? rid = arch switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => null,
        };

        if (rid is not null)
            yield return Path.Combine(baseDirectory, "native", rid, file);
        yield return Path.Combine(baseDirectory, file);
        if (rid is not null)
            yield return Path.Combine(baseDirectory, "runtimes", rid, "native", file);
    }
}
