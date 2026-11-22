using System.Runtime.InteropServices;
using CalcLink.Core.Native;
using CalcLink.Core.Simulation;

namespace CalcLink.Core.Tests;

public class FactoryAndNativeTests
{
    [Fact]
    public void Simulator_IsReturnedWhenRequested()
        => Assert.IsType<SimulatedCalculatorService>(CalculatorServiceFactory.Create(CalculatorBackend.Simulator));

    [Fact]
    public void IsNativeAvailable_NeverThrows_AndAutoIsConsistent()
    {
        bool available = CalculatorServiceFactory.IsNativeAvailable;
        Assert.Equal(available, CalculatorServiceFactory.IsNativeAvailable); // cached

        if (!available)
        {
            Assert.IsType<SimulatedCalculatorService>(CalculatorServiceFactory.Create(CalculatorBackend.Auto));
            Assert.False(string.IsNullOrEmpty(CalculatorServiceFactory.NativeUnavailableReason));
            Assert.ThrowsAny<Exception>(() => CalculatorServiceFactory.Create(CalculatorBackend.Native));
        }
    }

    [Fact]
    public void NativeDll_IsAbsentFromTestOutput_SoAutoFallsBack()
    {
        // The test project never copies calclink_native.dll; unless a developer points
        // CALCLINK_NATIVE_PATH at one, the native backend must be reported unavailable and Auto must
        // pick the simulator.
        if (Environment.GetEnvironmentVariable(NativeRuntime.PathEnvironmentVariable) is not null
            || NativeRuntime.CandidatePaths(AppContext.BaseDirectory, null, RuntimeInformation.ProcessArchitecture).Any(File.Exists))
            return;
        Assert.False(CalculatorServiceFactory.IsNativeAvailable);
        Assert.IsType<SimulatedCalculatorService>(CalculatorServiceFactory.Create());
    }

    [Fact]
    public void Auto_FallsBackWhenNativeUnavailable()
    {
        bool nativeCreated = false;
        var svc = CalculatorServiceFactory.Create(CalculatorBackend.Auto, () => false,
            () => { nativeCreated = true; throw new InvalidOperationException(); },
            () => new SimulatedCalculatorService(SimulatorOptions.Instant));
        Assert.IsType<SimulatedCalculatorService>(svc);
        Assert.False(nativeCreated);
    }

    [Fact]
    public void Auto_FallsBackWhenNativeInitFails()
    {
        var svc = CalculatorServiceFactory.Create(CalculatorBackend.Auto, () => true,
            () => throw new CalcLinkException(5, "cl_init failed"),
            () => new SimulatedCalculatorService(SimulatorOptions.Instant));
        Assert.IsType<SimulatedCalculatorService>(svc);
    }

    [Fact]
    public void Native_DoesNotFallBack()
    {
        Assert.Throws<DllNotFoundException>(() => CalculatorServiceFactory.Create(CalculatorBackend.Native, () => false,
            () => throw new DllNotFoundException("calclink"), () => new SimulatedCalculatorService()));
    }

    [Fact]
    public void CandidatePaths_SearchRidFolderThenAppDir()
    {
        string baseDir = Path.Combine("C:", "app");
        var arm = NativeRuntime.CandidatePaths(baseDir, null, Architecture.Arm64).ToList();
        Assert.Equal(
        [
            Path.Combine(baseDir, "native", "win-arm64", "calclink_native.dll"),
            Path.Combine(baseDir, "calclink_native.dll"),
            Path.Combine(baseDir, "runtimes", "win-arm64", "native", "calclink_native.dll"),
        ], arm);

        var x64 = NativeRuntime.CandidatePaths(baseDir, @"D:\custom\calclink_native.dll", Architecture.X64).ToList();
        Assert.Equal(@"D:\custom\calclink_native.dll", x64[0]);
        Assert.Equal(Path.Combine(baseDir, "native", "win-x64", "calclink_native.dll"), x64[1]);

        // Never probe "calclink.dll": on a case-insensitive file system that is the managed CalcLink.dll.
        Assert.DoesNotContain(arm.Concat(x64), p => Path.GetFileName(p).Equals("calclink.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("calclink_native", NativeMethods.LibraryName);
    }

    [Fact]
    public void Resolver_SkipsModulesWithoutTheAbiExport()
    {
        // A managed assembly (this test DLL) renamed into place must not be bound as the native library.
        using var dir = new TempDir();
        File.Copy(typeof(FactoryAndNativeTests).Assembly.Location, dir.File("calclink_native.dll"));
        Assert.True(NativeLibrary.TryLoad(dir.File("calclink_native.dll"), out nint h));
        try
        {
            Assert.False(NativeLibrary.TryGetExport(h, "cl_abi_version", out _));
        }
        finally
        {
            NativeLibrary.Free(h);
        }
    }

    [Theory]
    [InlineData(TiFileClass.Variable, CalcFeatures.Vars)]
    [InlineData(TiFileClass.TiGroup, CalcFeatures.Vars)]
    [InlineData(TiFileClass.App, CalcFeatures.Flash)]
    [InlineData(TiFileClass.Os, CalcFeatures.Os)]
    [InlineData(TiFileClass.Backup, CalcFeatures.Backup)]
    public void SendGating_PerFileClass(TiFileClass cls, CalcFeatures expected)
        => Assert.Equal(expected, NativeCalculatorSession.RequiredFeatureFor(cls, "x"));

    [Fact]
    public void SendGating_UnknownFile_IsBadFile()
    {
        var ex = Assert.Throws<CalcLinkException>(() => NativeCalculatorSession.RequiredFeatureFor(TiFileClass.Unknown, "x.txt"));
        Assert.Equal(CalcErrorCodes.BadFile, ex.Code);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(50, true)]
    [InlineData(268, true)]
    [InlineData(-4, true)]
    [InlineData(4, false)]   // read timeout: calculator may just be busy/asleep
    [InlineData(-5, false)]  // cancelled
    [InlineData(263, false)] // var rejected
    public void DisconnectClassification(int code, bool disconnect)
        => Assert.Equal(disconnect, CalcErrorCodes.IsDisconnect(code));
}

public class SerialWorkerTests
{
    [Fact]
    public async Task RunsEverythingOnOneThread_InOrder()
    {
        using var worker = new SerialWorker("test");
        var ids = new List<int>();
        var tasks = Enumerable.Range(0, 20).Select(i => worker.RunAsync(() =>
        {
            ids.Add(Environment.CurrentManagedThreadId);
            return i;
        })).ToList();
        int[] results = await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 20), results);
        Assert.Single(ids.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, ids[0]);
    }

    [Fact]
    public async Task CancelWhileQueued_NeverRuns()
    {
        using var worker = new SerialWorker("test");
        using var gate = new ManualResetEventSlim();
        var blocker = worker.RunAsync(() => gate.Wait());
        using var cts = new CancellationTokenSource();
        bool ran = false;
        var queued = worker.RunAsync(() => ran = true, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        gate.Set();
        await blocker;
        await worker.RunAsync(() => { });
        Assert.False(ran);
    }

    [Fact]
    public async Task Exceptions_FaultTheTask_NotTheThread()
    {
        using var worker = new SerialWorker("test");
        await Assert.ThrowsAsync<CalcLinkException>(() => worker.RunAsync<int>(() => throw new CalcLinkException(1, "x")));
        Assert.Equal(42, await worker.RunAsync(() => 42));
    }

    [Fact]
    public async Task AfterComplete_RejectsWork()
    {
        var worker = new SerialWorker("test");
        worker.Dispose();
        await Task.Yield();
        Assert.Throws<ObjectDisposedException>(() => { _ = worker.RunAsync(() => 1); });
    }
}
