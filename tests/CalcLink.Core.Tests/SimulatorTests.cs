using System.IO.Compression;
using CalcLink.Core.Simulation;

namespace CalcLink.Core.Tests;

public class SimulatorTests
{
    private static SimulatedCalculatorService NewService(SimulatorOptions? options = null)
        => new(options ?? SimulatorOptions.Instant);

    private static async Task<ICalculatorSession> Connect(SimulatedCalculatorService svc, CalcModel model)
    {
        var devices = await svc.ProbeAsync();
        return await svc.ConnectAsync(devices.Single(d => d.Model == model));
    }

    /// <summary>Reports synchronously, so tests can react deterministically.</summary>
    private sealed class SyncProgress(Action<TransferProgress> onReport) : IProgress<TransferProgress>
    {
        public List<TransferProgress> Reports { get; } = [];
        public void Report(TransferProgress value) { Reports.Add(value); onReport(value); }
    }

    [Fact]
    public async Task Probe_FindsThreeDevices()
    {
        var svc = NewService();
        var devices = await svc.ProbeAsync();
        Assert.Equal(3, devices.Count);
        Assert.Contains(devices, d => d.Model == CalcModel.TI84PCE_USB && d.Cable == CableModel.DirectLink && d.VendorId == 0x0451);
        Assert.Contains(devices, d => d.Model == CalcModel.Nspire);
        Assert.Contains(devices, d => d.Model == CalcModel.TI89T_USB);
        Assert.Equal("Simulator", svc.BackendName);
    }

    [Fact]
    public async Task Features_MirrorTilibs()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        Assert.True(ce.Supports(CalcFeatures.Screen | CalcFeatures.Clock | CalcFeatures.DirList | CalcFeatures.Vars
                                | CalcFeatures.Flash | CalcFeatures.Os | CalcFeatures.Version | CalcFeatures.DeleteVar | CalcFeatures.Backup));
        Assert.False(ce.Supports(CalcFeatures.Rename));
        Assert.False(ce.Supports(CalcFeatures.ChangeAttr));
        Assert.False(ce.Supports(CalcFeatures.IdList));
        Assert.False(ce.Supports(CalcFeatures.Folders));

        await using var nsp = await Connect(svc, CalcModel.Nspire);
        Assert.True(nsp.Supports(CalcFeatures.Folders | CalcFeatures.NewFolder | CalcFeatures.Rename));
        Assert.False(nsp.Supports(CalcFeatures.Clock));
        Assert.False(nsp.Supports(CalcFeatures.Backup));

        await using var ti89 = await Connect(svc, CalcModel.TI89T_USB);
        Assert.True(ti89.Supports(CalcFeatures.Folders | CalcFeatures.Rename | CalcFeatures.ChangeAttr | CalcFeatures.Clock));
    }

    [Fact]
    public async Task GetInfo_IsPlausible()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        CalcInfo info = await ce.GetInfoAsync();
        Assert.Equal(CalcModel.TI84PCE_USB, info.Model);
        Assert.Equal((320, 240), (info.LcdWidth, info.LcdHeight));
        Assert.True(info.PythonOnBoard);
        Assert.True(info.ColorScreen);
        Assert.InRange(info.RamFree, 1ul, info.RamUser);
        Assert.InRange(info.FlashFree, 1ul, info.FlashUser);

        await using var ti89 = await Connect(svc, CalcModel.TI89T_USB);
        CalcInfo info89 = await ti89.GetInfoAsync();
        Assert.Equal((160, 100), (info89.LcdWidth, info89.LcdHeight));
        Assert.False(info89.ColorScreen);
    }

    [Fact]
    public async Task Connect_SameDeviceTwice_IsRejected_UntilDisposed()
    {
        var svc = NewService();
        var device = (await svc.ProbeAsync()).First();
        var first = await svc.ConnectAsync(device);
        var ex = await Assert.ThrowsAsync<CalcLinkException>(() => svc.ConnectAsync(device));
        Assert.Equal(CalcErrorCodes.CableBusy, ex.Code);
        await first.DisposeAsync();
        await using var second = await svc.ConnectAsync(device);
        Assert.True(await second.IsReadyAsync());
    }

    [Fact]
    public async Task Variables_IncludeProgramsAppVarsAppsAndFolders()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var vars = await ce.GetVariablesAsync();
        Assert.Contains(vars, v => v.TypeName == "PRGM" && v.FileExtension == "8xp");
        Assert.Contains(vars, v => v.TypeName == "APPV" && v.Name == "SNAKE");
        Assert.Contains(vars, v => v.TypeName == "LIST");
        Assert.Contains(vars, v => v.TypeName == "MAT");
        Assert.Contains(vars, v => v.TypeName == "PIC");
        Assert.Contains(vars, v => v.IsApp && v.FileExtension == "8ek" && v.Attribute == VariableAttribute.Archived);
        Assert.All(vars, v => Assert.Equal("", v.Folder));

        await using var nsp = await Connect(svc, CalcModel.Nspire);
        var docs = await nsp.GetVariablesAsync();
        Assert.Contains(docs, v => v.Folder == "Python" && v.Name == "snake.py" && v.FileExtension == "tns");
        Assert.True(docs.Select(v => v.Folder).Distinct().Count() >= 3);

        await using var ti89 = await Connect(svc, CalcModel.TI89T_USB);
        var vars89 = await ti89.GetVariablesAsync();
        Assert.Contains(vars89, v => v.FullName == "main\\quadf");
        Assert.Contains(vars89, v => v.Folder == "games" && v.TypeName == "ASM");
    }

    [Fact]
    public async Task FeatureGating_ThrowsFeatureNotSupported()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var vars = await ce.GetVariablesAsync();
        var prog = vars.First(v => v.Name == "QUADFORM");

        var ex = await Assert.ThrowsAsync<FeatureNotSupportedException>(() => ce.RenameAsync(prog, "", "NEWNAME"));
        Assert.Equal(CalcFeatures.Rename, ex.Feature);
        Assert.Equal(CalcErrorCodes.NotSupported, ex.Code);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => ce.SetAttributeAsync(prog, VariableAttribute.Archived));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => ce.CreateFolderAsync("FOO"));

        await using var nsp = await Connect(svc, CalcModel.Nspire);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => nsp.GetClockAsync());
        using var dir = new TempDir();
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => nsp.BackupAsync(dir.File("b.tig")));

        // The returned task is faulted; the call itself does not throw synchronously.
        Task t = ce.RenameAsync(prog, "", "X");
        Assert.True(t.IsFaulted);
    }

    [Fact]
    public async Task SendAndReceive_RoundTripWithProgress()
    {
        using var dir = new TempDir();
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var vars = await ce.GetVariablesAsync();
        var picks = vars.Where(v => v.Name is "QUADFORM" or "SNAKE" or "L₁").ToList();

        var progress = new SyncProgress(_ => { });
        var paths = await ce.ReceiveAsync(picks, dir.Path, progress);

        Assert.Equal(3, paths.Count);
        Assert.Contains(Path.Combine(dir.Path, "QUADFORM.8xp"), paths);
        Assert.Contains(Path.Combine(dir.Path, "SNAKE.8xv"), paths);
        Assert.Contains(Path.Combine(dir.Path, "L₁.8xl"), paths);
        Assert.All(paths, p => Assert.True(new FileInfo(p).Length > 0));
        Assert.StartsWith("**TI83F*", File.ReadAllText(Path.Combine(dir.Path, "QUADFORM.8xp"))[..8]);
        Assert.NotEmpty(progress.Reports);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);

        // Receiving again into the same folder does not overwrite.
        var again = await ce.ReceiveAsync([picks[0]], dir.Path);
        Assert.EndsWith(" (2)" + Path.GetExtension(again[0]), again[0]);

        // Delete the program, then send the received file back: it reappears with its original name.
        var quad = picks.Single(v => v.Name == "QUADFORM");
        await ce.DeleteAsync([quad]);
        Assert.DoesNotContain(await ce.GetVariablesAsync(), v => v.Name == "QUADFORM");

        var sendProgress = new SyncProgress(_ => { });
        await ce.SendFilesAsync([Path.Combine(dir.Path, "QUADFORM.8xp")], sendProgress);
        var after = await ce.GetVariablesAsync();
        var restored = Assert.Single(after, v => v.Name == "QUADFORM");
        Assert.Equal(quad.Size, restored.Size);
        Assert.Equal("PRGM", restored.TypeName);
        Assert.Contains(sendProgress.Reports, r => r.Status.Contains("QUADFORM.8xp"));
    }

    [Fact]
    public async Task SendFile_NamedAfterFile_WhenNoHeader()
    {
        using var dir = new TempDir();
        string path = dir.File("Mylab.tns");
        await File.WriteAllBytesAsync(path, new byte[5000]);
        var svc = NewService();
        await using var nsp = await Connect(svc, CalcModel.Nspire);
        await nsp.SendFilesAsync([path]);
        var docs = await nsp.GetVariablesAsync();
        var doc = Assert.Single(docs, v => v.Name == "Mylab");
        Assert.Equal(5000u, doc.Size);
    }

    [Fact]
    public async Task SendFile_IncompatibleOrUnknown_IsRejected()
    {
        using var dir = new TempDir();
        string tns = dir.File("doc.tns");
        string junk = dir.File("readme.txt");
        File.WriteAllText(tns, "x");
        File.WriteAllText(junk, "x");
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var ex1 = await Assert.ThrowsAsync<CalcLinkException>(() => ce.SendFilesAsync([tns]));
        Assert.Equal(CalcErrorCodes.BadFile, ex1.Code);
        var ex2 = await Assert.ThrowsAsync<CalcLinkException>(() => ce.SendFilesAsync([junk]));
        Assert.Equal(CalcErrorCodes.BadFile, ex2.Code);
    }

    [Fact]
    public async Task Receive_Cancellation_StopsAndThrows()
    {
        using var dir = new TempDir();
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var vars = await ce.GetVariablesAsync();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ce.ReceiveAsync(vars.Take(3).ToList(), dir.Path, progress, cts.Token));
        Assert.Single(progress.Reports);
        Assert.Empty(Directory.GetFiles(dir.Path));

        // The session is still usable afterwards.
        Assert.True(await ce.IsReadyAsync());
    }

    [Fact]
    public async Task Send_Cancellation_LeavesStateUnchanged()
    {
        using var dir = new TempDir();
        string path = dir.File("NEWPRGM.8xp");
        await File.WriteAllBytesAsync(path, new byte[20_000]);
        var svc = NewService(SimulatorOptions.Instant with { BytesPerSecond = 200_000, MaxTransferTime = TimeSpan.FromSeconds(2) });
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Fraction > 0.2) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ce.SendFilesAsync([path], progress, cts.Token));
        Assert.DoesNotContain(await ce.GetVariablesAsync(), v => v.Name == "NEWPRGM");
        Assert.True(progress.Reports.Count >= 2);
    }

    [Fact]
    public async Task AlreadyCancelledToken_DoesNothing()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var vars = await ce.GetVariablesAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ce.DeleteAsync(vars, new CancellationToken(true)));
        Assert.Equal(vars.Count, (await ce.GetVariablesAsync()).Count);
    }

    [Fact]
    public async Task Rename_Delete_SetAttribute_CreateFolder_MutateState()
    {
        var svc = NewService();
        await using var ti89 = await Connect(svc, CalcModel.TI89T_USB);
        var vars = await ti89.GetVariablesAsync();

        await ti89.CreateFolderAsync("work");
        await Assert.ThrowsAsync<CalcLinkException>(() => ti89.CreateFolderAsync("work"));

        var quadf = vars.Single(v => v.FullName == "main\\quadf");
        await ti89.RenameAsync(quadf, "work", "quad2");
        var notes = vars.Single(v => v.Name == "notes");
        await ti89.SetAttributeAsync(notes, VariableAttribute.Archived);
        var a = vars.Single(v => v.FullName == "main\\a");
        await ti89.DeleteAsync([a]);

        var after = await ti89.GetVariablesAsync();
        Assert.Contains(after, v => v.FullName == "work\\quad2" && v.TypeName == "PRGM");
        Assert.DoesNotContain(after, v => v.FullName == "main\\quadf");
        Assert.DoesNotContain(after, v => v.FullName == "main\\a");
        Assert.Equal(VariableAttribute.Archived, after.Single(v => v.Name == "notes").Attribute);

        // Renaming onto an existing name, or into a missing folder, fails cleanly.
        var f = after.Single(v => v.FullName == "main\\f");
        await Assert.ThrowsAsync<CalcLinkException>(() => ti89.RenameAsync(f, "main", "l1"));
        await Assert.ThrowsAsync<CalcLinkException>(() => ti89.RenameAsync(f, "nosuch", "g"));
    }

    [Fact]
    public async Task State_PersistsAcrossReconnect()
    {
        var svc = NewService();
        var device = (await svc.ProbeAsync()).Single(d => d.Model == CalcModel.Nspire);
        await using (var s = await svc.ConnectAsync(device))
        {
            var doc = (await s.GetVariablesAsync()).First(v => v.Name == "Calculus");
            await s.RenameAsync(doc, "MyLib", "Calc BC");
        }
        await using var s2 = await svc.ConnectAsync(device);
        Assert.Contains(await s2.GetVariablesAsync(), v => v.FullName == "MyLib\\Calc BC");
    }

    [Fact]
    public async Task StaleTokens_AfterRefresh_AreRejected()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var old = await ce.GetVariablesAsync();
        _ = await ce.GetVariablesAsync(); // refresh invalidates `old`
        var ex = await Assert.ThrowsAsync<CalcLinkException>(() => ce.DeleteAsync([old[0]]));
        Assert.Equal(CalcErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("outdated", ex.Message);

        // Variables from another session are rejected too.
        await using var nsp = await Connect(svc, CalcModel.Nspire);
        var fresh = await ce.GetVariablesAsync();
        await Assert.ThrowsAsync<CalcLinkException>(() => nsp.DeleteAsync([fresh[0]]));
    }

    [Fact]
    public async Task Screen_HasModelResolution_AndAnimates()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var a = await ce.CaptureScreenAsync();
        Assert.Equal((320, 240), (a.Width, a.Height));
        Assert.Equal(320 * 240 * 4, a.Bgra.Length);

        var device = SimDevice.Create(SimulatedModel.TI84PlusCEPython, 1);
        var f1 = SimulatedScreen.Render(device, 1.0);
        var f2 = SimulatedScreen.Render(device, 1.5);
        Assert.NotEqual(f1.Bgra, f2.Bgra);

        await using var ti89 = await Connect(svc, CalcModel.TI89T_USB);
        var s89 = await ti89.CaptureScreenAsync();
        Assert.Equal((160, 100), (s89.Width, s89.Height));
        Assert.True(s89.Bgra.Where((_, i) => i % 4 == 3).All(alpha => alpha == 255));

        await using var nsp = await Connect(svc, CalcModel.Nspire);
        var sn = await nsp.CaptureScreenAsync();
        Assert.Equal((320, 240), (sn.Width, sn.Height));
    }

    [Fact]
    public async Task Clock_TicksAndCanBeSet()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var target = new DateTime(2030, 1, 2, 3, 4, 5);
        await ce.SetClockAsync(new CalcClock(target, 12, 2));
        var c = await ce.GetClockAsync();
        Assert.InRange(c.Value, target.AddSeconds(-1), target.AddSeconds(5));
        Assert.Equal(12, c.TimeFormat);
        Assert.Equal(2, c.DateFormat);
    }

    [Fact]
    public async Task Backup_TiGroup_IsAZipOfVariableFiles()
    {
        using var dir = new TempDir();
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var progress = new SyncProgress(_ => { });
        string path = dir.File("backup.tig");
        await ce.BackupAsync(path, progress);

        using var zip = ZipFile.OpenRead(path);
        Assert.Contains(zip.Entries, e => e.Name == "QUADFORM.8xp");
        Assert.Contains(zip.Entries, e => e.Name == "CabriJr.8ek");
        Assert.True(progress.Reports.Count > 0);
        Assert.Equal(1.0, progress.Reports[^1].Fraction);

        // ...and it can be sent back.
        await ce.SendFilesAsync([path]);

        string native = dir.File("backup.8cg");
        await ce.BackupAsync(native);
        Assert.True(new FileInfo(native).Length > 1000);
    }

    [Fact]
    public async Task DumpRom_WritesFullSizeImage()
    {
        using var dir = new TempDir();
        var svc = NewService();
        await using var ti89 = await Connect(svc, CalcModel.TI89T_USB);
        string path = dir.File("rom.rom");
        await ti89.DumpRomAsync(path);
        Assert.Equal(4L * 1024 * 1024, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Execute_OnlyForExecutableTypes()
    {
        var svc = NewService();
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var vars = await ce.GetVariablesAsync();
        await ce.ExecuteAsync(vars.First(v => v.Name == "QUADFORM"));
        await Assert.ThrowsAsync<CalcLinkException>(() => ce.ExecuteAsync(vars.First(v => v.TypeName == "LIST")));
    }

    [Fact]
    public async Task FaultInjection_RaisesDisconnected_AndUnplugsDevice()
    {
        var svc = NewService(SimulatorOptions.Instant with { DisconnectAfterOperations = 2, ReplugDelay = TimeSpan.FromMinutes(5) });
        var session = await Connect(svc, CalcModel.TI84PCE_USB);
        var raised = new TaskCompletionSource<CalcLinkException>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, e) => raised.TrySetResult(e);

        Assert.True(await session.IsReadyAsync()); // op 1
        var ex = await Assert.ThrowsAsync<CalcLinkException>(() => session.GetInfoAsync()); // op 2 fails
        Assert.True(CalcErrorCodes.IsDisconnect(ex.Code));

        var evt = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(ex, evt);

        // Further operations fail fast; the device vanished from probes.
        var ex2 = await Assert.ThrowsAsync<CalcLinkException>(() => session.IsReadyAsync());
        Assert.Equal(CalcErrorCodes.NotOpen, ex2.Code);
        Assert.DoesNotContain(await svc.ProbeAsync(), d => d.Model == CalcModel.TI84PCE_USB);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FaultInjection_DuringTransfer_FailsHalfway()
    {
        using var dir = new TempDir();
        var svc = NewService(SimulatorOptions.Instant with { DisconnectAfterOperations = 2 });
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var vars = await ce.GetVariablesAsync(); // op 1
        var progress = new SyncProgress(_ => { });
        var ex = await Assert.ThrowsAsync<CalcLinkException>(
            () => ce.ReceiveAsync([vars.First(v => v.Name == "CESIUM")], dir.Path, progress)); // op 2
        Assert.Equal(CalcErrorCodes.CableWriteError, ex.Code);
        Assert.InRange(progress.Reports[^1].Fraction ?? 0, 0.1, 0.9);
    }

    [Fact]
    public async Task ManualDisconnect_RaisesEventOnce()
    {
        var svc = NewService();
        var session = (SimulatedCalculatorSession)await Connect(svc, CalcModel.Nspire);
        int count = 0;
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, _) => { Interlocked.Increment(ref count); raised.TrySetResult(); };
        session.SimulateDisconnect();
        session.SimulateDisconnect();
        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(1, count);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Disposed_Session_Throws()
    {
        var svc = NewService();
        var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        await ce.DisposeAsync();
        await ce.DisposeAsync(); // idempotent
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ce.IsReadyAsync());
    }

    [Fact]
    public async Task Delays_AreApplied_WhenConfigured()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant with { CommandDelay = TimeSpan.FromMilliseconds(40) });
        await using var ce = await Connect(svc, CalcModel.TI84PCE_USB);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ce.IsReadyAsync();
        Assert.True(sw.ElapsedMilliseconds >= 30, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("prog.8xp", TiFileClass.Variable, CalcModel.TI83P)]
    [InlineData("app.8ek", TiFileClass.App, CalcModel.TI84PCE_USB)]
    [InlineData("os.8eu", TiFileClass.Os, CalcModel.TI84PCE_USB)]
    [InlineData("doc.tns", TiFileClass.Variable, CalcModel.Nspire)]
    [InlineData("os.tco2", TiFileClass.Os, CalcModel.Nspire)]
    [InlineData("all.tig", TiFileClass.TiGroup, CalcModel.None)]
    [InlineData("cert.89q", TiFileClass.Certificate, CalcModel.TI89)]
    [InlineData("readme.txt", TiFileClass.Unknown, CalcModel.None)]
    public void ClassifyFile_ByExtension(string file, TiFileClass expected, CalcModel expectedModel)
    {
        var svc = NewService();
        Assert.Equal(expected, svc.ClassifyFile(file, out CalcModel model));
        Assert.Equal(expectedModel, model);
    }
}
