using System.IO.Compression;
using System.Text;
using CalcLink.Core.Native;

namespace CalcLink.Core.Simulation;

/// <summary><see cref="CalcVariable.BackendToken"/> for the simulator. Mirrors the native token's lifetime rules.</summary>
internal sealed class SimVarToken(SimulatedCalculatorSession owner, int generation, long id)
{
    public SimulatedCalculatorSession Owner { get; } = owner;
    public int Generation { get; } = generation;
    public long Id { get; } = id;
}

/// <summary>
/// A session with a simulated calculator. Operations are serialized (like a real link), honour
/// cancellation, report progress with realistic pacing (see <see cref="SimulatorOptions"/>), and
/// follow the same rules as the native backend: feature gating, stale-token detection after a
/// list refresh, and a <see cref="Disconnected"/> event on (injected) link loss.
/// </summary>
public sealed class SimulatedCalculatorSession : ICalculatorSession
{
    private readonly SimulatedCalculatorService _service;
    private readonly SimDevice _d;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _generation;
    private int _operations;
    private volatile bool _lost;
    private bool _pendingFault;
    private bool _holdsDevice = true;
    private int _disposed;
    private int _disconnectRaised;

    internal SimulatedCalculatorSession(SimulatedCalculatorService service, SimDevice device)
    {
        _service = service;
        _d = device;
    }

    private SimulatorOptions Options => _service.Options;

    public DeviceDescriptor Device => _d.Descriptor;
    public CalcModel Model => _d.Descriptor.Model;
    public CalcFeatures Features => _d.Features;

    public event EventHandler<CalcLinkException>? Disconnected;

    /// <summary>Fault injection: behave as if the USB cable was pulled right now.</summary>
    public void SimulateDisconnect()
        => MarkLost(new CalcLinkException(CalcErrorCodes.CableNoCable, $"{Device.DisplayName} was unplugged (simulated)."));

    // ------------------------------------------------------------------ plumbing

    private async Task<T> Op<T>(CalcFeatures required, Func<Task<T>> body, CancellationToken ct, bool isTransfer = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if ((Features & required) != required)
            throw new FeatureNotSupportedException(required, Model);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_lost)
                throw new CalcLinkException(CalcErrorCodes.NotOpen, $"The link to {Device.DisplayName} was lost. Reconnect and try again.");

            int n = ++_operations;
            bool fault = (Options.DisconnectAfterOperations is int k && n >= k)
                         || _service.NextFault(Options.RandomDisconnectProbability);
            if (fault)
            {
                if (!isTransfer)
                    throw Lose();
                _pendingFault = true; // fail half-way through the transfer instead
            }
            return await body().ConfigureAwait(false);
        }
        finally
        {
            _pendingFault = false;
            _gate.Release();
        }
    }

    private Task Op(CalcFeatures required, Func<Task> body, CancellationToken ct, bool isTransfer = false)
        => Op(required, async () => { await body().ConfigureAwait(false); return true; }, ct, isTransfer);

    private CalcLinkException Lose()
    {
        var ex = new CalcLinkException(CalcErrorCodes.CableWriteError,
            $"Write error: {Device.DisplayName} stopped responding (simulated disconnect).");
        MarkLost(ex);
        return ex;
    }

    private void MarkLost(CalcLinkException ex)
    {
        _lost = true;
        lock (_d.Gate)
        {
            if (_holdsDevice)
            {
                _d.InUse = false;
                _holdsDevice = false;
            }
            _d.UnpluggedUntilTicks = Environment.TickCount64 + (long)Options.ReplugDelay.TotalMilliseconds;
        }
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static s =>
            {
                var (self, error) = s;
                try { self.Disconnected?.Invoke(self, error); } catch { /* ignore handler failures */ }
            }, (this, ex), preferLocal: false);
        }
    }

    private Task Command(CancellationToken ct) => SimDelay.Wait(Options.CommandDelay, ct);

    /// <summary>Paces a transfer of <paramref name="bytes"/> and reports progress (bytes).</summary>
    private async Task Transfer(long bytes, string status, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        bytes = Math.Max(1, bytes);
        double seconds = Options.BytesPerSecond > 0 ? bytes / Options.BytesPerSecond : 0;
        seconds = Math.Min(seconds, Math.Max(0, Options.MaxTransferTime.TotalSeconds));
        int steps = (int)Math.Clamp(bytes / 1024, 4, 50);
        TimeSpan stepDelay = TimeSpan.FromSeconds(seconds / steps);
        double rateKbps = seconds > 0 ? bytes / seconds / 1024.0 : 0;

        progress?.Report(new TransferProgress(0, bytes, 0, status));
        for (int i = 1; i <= steps; i++)
        {
            await SimDelay.Wait(stepDelay, ct).ConfigureAwait(false);
            if (_pendingFault && i * 2 >= steps)
            {
                _pendingFault = false;
                throw Lose();
            }
            progress?.Report(new TransferProgress(bytes * i / steps, bytes, rateKbps, status));
        }
    }

    private SimVar Resolve(CalcVariable variable)
    {
        if (variable.BackendToken is not SimVarToken token || token.Owner != this)
            throw new CalcLinkException(CalcErrorCodes.InvalidArgument,
                $"'{variable.FullName}' was not listed by this connection.");
        if (token.Generation != _generation)
            throw new CalcLinkException(CalcErrorCodes.InvalidArgument,
                $"'{variable.FullName}' comes from an outdated variable list. Refresh the list and try again.");
        lock (_d.Gate)
            return _d.Vars.FirstOrDefault(v => v.Id == token.Id)
                   ?? throw new CalcLinkException(CalcErrorCodes.CalcMissingVar,
                       $"'{variable.FullName}' no longer exists on the calculator.");
    }

    private CalcVariable ToModel(SimVar v) => new()
    {
        Folder = v.Folder,
        Name = v.Name,
        TypeName = v.TypeName,
        FileExtension = v.Extension,
        Size = v.Size,
        RawType = v.RawType,
        Attribute = v.Attribute,
        IsApp = v.IsApp,
        BackendToken = new SimVarToken(this, _generation, v.Id),
    };

    private static string Prefix(string item, int index, int count) => count > 1 ? $"{item} ({index + 1}/{count})" : item;

    // ------------------------------------------------------------------ ICalculatorSession

    public Task<bool> IsReadyAsync(CancellationToken ct = default)
        => Op(CalcFeatures.IsReady, async () => { await Command(ct).ConfigureAwait(false); return true; }, ct);

    public Task<CalcInfo> GetInfoAsync(CancellationToken ct = default)
        => Op(CalcFeatures.Version, async () =>
        {
            await Command(ct).ConfigureAwait(false);
            lock (_d.Gate)
            {
                ulong ramUsed = _d.RamUsed, flashUsed = _d.FlashUsed;
                return new CalcInfo(
                    Model, _d.ProductName, _d.ProductId, _d.OsVersion, _d.BootVersion,
                    _d.RamUser, _d.RamUser > ramUsed ? _d.RamUser - ramUsed : 0,
                    _d.FlashUser, _d.FlashUser > flashUsed ? _d.FlashUser - flashUsed : 0,
                    _d.LcdWidth, _d.LcdHeight, _d.BitsPerPixel,
                    BatteryOk: true, ColorScreen: _d.ColorScreen, PythonOnBoard: _d.PythonOnBoard,
                    ExactMath: _d.ExactMath, ClockSupport: _d.HasClock, RunLevel: 2);
            }
        }, ct);

    public Task<IReadOnlyList<CalcVariable>> GetVariablesAsync(CancellationToken ct = default)
        => Op<IReadOnlyList<CalcVariable>>(CalcFeatures.DirList, async () =>
        {
            await Command(ct).ConfigureAwait(false);
            _generation++; // like the native backend, a refresh invalidates older CalcVariables
            lock (_d.Gate)
                return _d.Vars
                    .OrderBy(v => v.IsApp)
                    .ThenBy(v => v.Folder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(ToModel)
                    .ToArray();
        }, ct);

    public Task SendFilesAsync(IReadOnlyList<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Op(CalcFeatures.None, async () =>
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string path = paths[i];
                string file = Path.GetFileName(path);
                TiFileClass cls = SimFiles.Classify(path, out _);
                CalcFeatures required = NativeCalculatorSession.RequiredFeatureFor(cls, path);
                if ((Features & required) != required)
                    throw new FeatureNotSupportedException(required, Model);

                byte[] content;
                try
                {
                    content = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new CalcLinkException(CalcErrorCodes.BadFile, $"Cannot read '{file}': {ex.Message}");
                }

                string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                List<SimVar> incoming = cls switch
                {
                    TiFileClass.Variable or TiFileClass.App => [ParseVariable(file, ext, content)],
                    TiFileClass.TiGroup => ParseTiGroup(file, content),
                    TiFileClass.Os when _d.AcceptsOs(ext) => [],
                    TiFileClass.Certificate when _d.AcceptsCert(ext) => [],
                    TiFileClass.Backup when IsOwnFamily(ext) => [],
                    _ => throw Incompatible(file),
                };
                CheckMemory(incoming);

                string verb = cls == TiFileClass.Os ? "Installing OS" : "Sending";
                await Transfer(content.Length, $"{Prefix(file, i, paths.Count)}: {verb}", progress, ct).ConfigureAwait(false);

                lock (_d.Gate)
                {
                    foreach (SimVar v in incoming)
                    {
                        if (v.Folder.Length > 0)
                            _d.Folders.Add(v.Folder);
                        SimVar? existing = _d.Find(v.Folder, v.Name);
                        if (existing is not null)
                            _d.Vars.Remove(existing); // silent overwrite, like tilibs' MODE_SEND_ONE_VAR
                        _d.Vars.Add(v);
                    }
                }
            }
        }, ct, isTransfer: true);
    }

    private CalcLinkException Incompatible(string file)
        => new(CalcErrorCodes.BadFile, $"'{file}' is not compatible with the {_d.ProductName}.");

    private bool IsOwnFamily(string ext) => _d.Kind switch
    {
        SimulatedModel.TI84PlusCEPython => ext.StartsWith("8c", StringComparison.Ordinal) || ext.StartsWith("8e", StringComparison.Ordinal),
        SimulatedModel.TI89Titanium => ext.StartsWith("89", StringComparison.Ordinal),
        _ => false,
    };

    private SimVar ParseVariable(string file, string ext, byte[] content)
    {
        SimType type = _d.TypeForExtension(ext) ?? throw Incompatible(file);
        string stem = Path.GetFileNameWithoutExtension(file);
        string name = stem;
        string folder = type.IsApp ? "" : _d.DefaultFolder;
        byte[] data = content;
        var attr = type.IsApp ? VariableAttribute.Archived : VariableAttribute.None;

        if (_d.Kind == SimulatedModel.TI84PlusCEPython && !type.IsApp && SimFiles.TryReadTi83F(content) is { } parsed)
        {
            name = parsed.Name.Length > 0 ? parsed.Name : stem;
            data = parsed.Body;
            if (parsed.Archived) attr = VariableAttribute.Archived;
        }
        else if (_d.Kind == SimulatedModel.TI89Titanium && !type.IsApp
                 && content.Length >= 18 && content.AsSpan(0, 8).SequenceEqual("**TI89**"u8))
        {
            var f = content.AsSpan(10, 8);
            int nul = f.IndexOf((byte)0);
            string fromFile = Encoding.ASCII.GetString(nul >= 0 ? f[..nul] : f);
            if (fromFile.Length > 0) folder = fromFile;
        }

        if (name.Length > _d.MaxNameLength)
            name = name[.._d.MaxNameLength];
        if (_d.Kind == SimulatedModel.TI89Titanium && !type.IsApp)
            name = name.ToLowerInvariant();

        return new SimVar
        {
            Folder = folder, Name = name, TypeName = type.TypeName, Extension = ext, RawType = type.RawType,
            IsApp = type.IsApp, Attribute = attr, Data = data,
        };
    }

    private List<SimVar> ParseTiGroup(string file, byte[] content)
    {
        var result = new List<SimVar>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string ext = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant();
                if (entry.Length == 0 || _d.TypeForExtension(ext) is null)
                    continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                result.Add(ParseVariable(entry.Name, ext, ms.ToArray()));
            }
        }
        catch (InvalidDataException)
        {
            throw new CalcLinkException(CalcErrorCodes.BadFile, $"'{file}' is not a valid TIGroup file.");
        }
        if (result.Count == 0)
            throw Incompatible(file);
        return result;
    }

    private void CheckMemory(IEnumerable<SimVar> incoming)
    {
        lock (_d.Gate)
        {
            ulong ram = 0, flash = 0;
            foreach (SimVar v in incoming)
            {
                SimVar? existing = _d.Find(v.Folder, v.Name);
                bool inFlash = v.IsApp || v.Attribute == VariableAttribute.Archived || _d.Kind == SimulatedModel.NspireCXII;
                ulong delta = v.Size - Math.Min(v.Size, existing?.Size ?? 0);
                if (inFlash) flash += delta; else ram += delta;
            }
            if (_d.RamUsed + ram > _d.RamUser || _d.FlashUsed + flash > _d.FlashUser)
                throw new CalcLinkException(CalcErrorCodes.CalcOutOfMemory, $"Not enough memory on the {_d.ProductName}.");
        }
    }

    public Task<IReadOnlyList<string>> ReceiveAsync(IReadOnlyList<CalcVariable> variables, string destinationFolder,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentException.ThrowIfNullOrEmpty(destinationFolder);
        CalcFeatures required = CalcFeatures.None;
        foreach (CalcVariable v in variables)
            required |= v.IsApp ? CalcFeatures.Flash : CalcFeatures.Vars;

        return Op<IReadOnlyList<string>>(required, async () =>
        {
            Directory.CreateDirectory(destinationFolder);
            var taken = CalcFileNames.NewPathSet();
            var written = new List<string>(variables.Count);
            for (int i = 0; i < variables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                CalcVariable variable = variables[i];
                SimVar v = Resolve(variable);
                byte[] bytes;
                lock (_d.Gate)
                    bytes = SimFiles.Serialize(_d, v);

                await Transfer(bytes.Length, $"{Prefix(variable.FullName, i, variables.Count)}: Receiving", progress, ct)
                    .ConfigureAwait(false);

                string path = CalcFileNames.MakeUnique(destinationFolder,
                    CalcFileNames.ForVariable(variable.Name, variable.FileExtension), taken);
                await File.WriteAllBytesAsync(path, bytes, CancellationToken.None).ConfigureAwait(false);
                written.Add(path);
            }
            return written;
        }, ct, isTransfer: true);
    }

    public Task DeleteAsync(IReadOnlyList<CalcVariable> variables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return Op(CalcFeatures.DeleteVar, async () =>
        {
            foreach (CalcVariable variable in variables)
            {
                ct.ThrowIfCancellationRequested();
                SimVar v = Resolve(variable);
                await Command(ct).ConfigureAwait(false);
                lock (_d.Gate)
                    _d.Vars.Remove(v);
            }
        }, ct);
    }

    public Task RenameAsync(CalcVariable variable, string newFolder, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        return Op(CalcFeatures.Rename, async () =>
        {
            SimVar v = Resolve(variable);
            await Command(ct).ConfigureAwait(false);
            if (v.IsApp)
                throw new CalcLinkException(CalcErrorCodes.CalcVarRejected, "Flash applications cannot be renamed.");
            ValidateName(newName, "variable");

            lock (_d.Gate)
            {
                string folder = !_d.HasFolders ? "" : string.IsNullOrEmpty(newFolder) ? (_d.DefaultFolder.Length > 0 ? v.Folder : "") : newFolder;
                if (folder.Length > 0 && !_d.Folders.Contains(folder))
                    throw new CalcLinkException(CalcErrorCodes.CalcMissingVar, $"Folder '{folder}' does not exist.");
                folder = _d.Folders.FirstOrDefault(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) ?? folder;
                SimVar? clash = _d.Find(folder, newName);
                if (clash is not null && clash != v)
                    throw new CalcLinkException(CalcErrorCodes.CalcVarRejected,
                        $"'{(folder.Length == 0 ? newName : folder + "\\" + newName)}' already exists.");
                v.Folder = folder;
                v.Name = newName;
            }
        }, ct);
    }

    private void ValidateName(string name, string what)
    {
        if (name.Length > _d.MaxNameLength || name.IndexOfAny(['\\', '/', '\0']) >= 0 || name.Trim().Length == 0)
            throw new CalcLinkException(CalcErrorCodes.InvalidArgument,
                $"'{name}' is not a valid {what} name on the {_d.ProductName} (max {_d.MaxNameLength} characters).");
    }

    public Task SetAttributeAsync(CalcVariable variable, VariableAttribute attribute, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return Op(CalcFeatures.ChangeAttr, async () =>
        {
            SimVar v = Resolve(variable);
            await Command(ct).ConfigureAwait(false);
            if (v.IsApp)
                throw new CalcLinkException(CalcErrorCodes.CalcVarRejected, "Flash applications are always archived.");
            lock (_d.Gate)
            {
                bool toFlash = attribute == VariableAttribute.Archived && v.Attribute != VariableAttribute.Archived;
                bool toRam = attribute != VariableAttribute.Archived && v.Attribute == VariableAttribute.Archived;
                if ((toFlash && _d.FlashUsed + v.Size > _d.FlashUser) || (toRam && _d.RamUsed + v.Size > _d.RamUser))
                    throw new CalcLinkException(CalcErrorCodes.CalcOutOfMemory, $"Not enough memory on the {_d.ProductName}.");
                v.Attribute = attribute;
            }
        }, ct);
    }

    public Task CreateFolderAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Op(CalcFeatures.NewFolder, async () =>
        {
            await Command(ct).ConfigureAwait(false);
            ValidateName(name, "folder");
            lock (_d.Gate)
            {
                if (!_d.Folders.Add(name))
                    throw new CalcLinkException(CalcErrorCodes.CalcVarRejected, $"Folder '{name}' already exists.");
            }
        }, ct);
    }

    public Task ExecuteAsync(CalcVariable variable, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return Op(CalcFeatures.None, async () =>
        {
            if (_d.Kind == SimulatedModel.NspireCXII)
                throw new CalcLinkException(CalcErrorCodes.NotSupported, $"Remote execution is not supported on the {_d.ProductName}.");
            SimVar v = Resolve(variable);
            if (_d.TypeForExtension(v.Extension) is not { Executable: true })
                throw new CalcLinkException(CalcErrorCodes.InvalidArgument, $"'{variable.FullName}' ({v.TypeName}) cannot be executed.");
            await Command(ct).ConfigureAwait(false);
        }, ct);
    }

    public Task SendKeyAsync(uint keyCode, CancellationToken ct = default)
        => Op(CalcFeatures.Keys, async () =>
        {
            await Command(ct).ConfigureAwait(false);
            lock (_d.Gate)
                _d.KeyPresses++;
        }, ct);

    public Task<ScreenImage> CaptureScreenAsync(CancellationToken ct = default)
        => Op(CalcFeatures.Screen, async () =>
        {
            // A CE screen is 150 KB uncompressed but tilibs gets it RLE-compressed: keep it snappy.
            await SimDelay.Wait(Options.CommandDelay * 2, ct).ConfigureAwait(false);
            lock (_d.Gate)
                return SimulatedScreen.Render(_d, _service.ElapsedSeconds);
        }, ct);

    public Task<CalcClock> GetClockAsync(CancellationToken ct = default)
        => Op(CalcFeatures.Clock, async () =>
        {
            await Command(ct).ConfigureAwait(false);
            lock (_d.Gate)
            {
                DateTime now = DateTime.Now + _d.ClockOffset;
                now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Unspecified);
                return new CalcClock(now, _d.TimeFormat, _d.DateFormat);
            }
        }, ct);

    public Task SetClockAsync(CalcClock clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Op(CalcFeatures.Clock, async () =>
        {
            await Command(ct).ConfigureAwait(false);
            lock (_d.Gate)
            {
                _d.ClockOffset = clock.Value - DateTime.Now;
                _d.TimeFormat = clock.TimeFormat == 12 ? 12 : 24;
                _d.DateFormat = clock.DateFormat;
            }
        }, ct);
    }

    public Task BackupAsync(string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Op(CalcFeatures.Backup, async () =>
        {
            SimVar[] vars;
            lock (_d.Gate)
                vars = [.. _d.Vars];

            bool tig = string.Equals(Path.GetExtension(path), ".tig", StringComparison.OrdinalIgnoreCase);
            if (!tig)
                vars = vars.Where(v => !v.IsApp).ToArray(); // native backups hold variables only

            // Receive everything first (progress over the whole backup), then write the file, so a
            // cancelled backup leaves nothing behind.
            long total = Math.Max(1, vars.Sum(v => (long)v.Size));
            long done = 0;
            for (int i = 0; i < vars.Length; i++)
            {
                SimVar v = vars[i];
                long before = done;
                var inner = progress is null ? null : new SyncProgress(p =>
                    progress.Report(new TransferProgress(before + p.Current * v.Size / Math.Max(1, p.Total), total, p.RateKbps,
                        $"Backup ({i + 1}/{vars.Length}): {v.FullName}")));
                await Transfer(v.Size, v.FullName, inner, ct).ConfigureAwait(false);
                done += v.Size;
            }

            byte[] bytes;
            lock (_d.Gate)
                bytes = tig ? WriteTiGroup(vars) : _d.Kind == SimulatedModel.TI84PlusCEPython
                    ? SimFiles.WriteTi83F(vars, "Backup by CalcLink (simulated)")
                    : SimFiles.Join("**TI89**"u8, [.. vars.Select(v => SimFiles.Serialize(_d, v))]);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllBytesAsync(path, bytes, CancellationToken.None).ConfigureAwait(false);
        }, ct, isTransfer: true);
    }

    private byte[] WriteTiGroup(IEnumerable<SimVar> vars)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SimVar v in vars)
            {
                string entryName = CalcFileNames.ForVariable(v.Name, v.Extension);
                if (v.Folder.Length > 0)
                    entryName = $"{CalcFileNames.Sanitize(v.Folder)}/{entryName}";
                string unique = entryName;
                for (int n = 2; !taken.Add(unique); n++)
                    unique = $"{Path.ChangeExtension(entryName, null)} ({n}){Path.GetExtension(entryName)}";
                ZipArchiveEntry entry = zip.CreateEntry(unique, CompressionLevel.Optimal);
                using Stream s = entry.Open();
                s.Write(SimFiles.Serialize(_d, v));
            }
        }
        return ms.ToArray();
    }

    public Task DumpRomAsync(string path, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Op(CalcFeatures.RomDump, async () =>
        {
            await Transfer((long)_d.RomSize, "Dumping ROM", progress, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] header = Encoding.ASCII.GetBytes($"SIMULATED ROM IMAGE - {_d.ProductName} - not a real dump\n");
            await fs.WriteAsync(header, CancellationToken.None).ConfigureAwait(false);
            fs.SetLength((long)_d.RomSize);
        }, ct, isTransfer: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Let a running operation finish (they all complete or observe cancellation promptly).
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_d.Gate)
            {
                if (_holdsDevice)
                {
                    _d.InUse = false;
                    _holdsDevice = false;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>An IProgress that reports synchronously on the calling thread.</summary>
    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
