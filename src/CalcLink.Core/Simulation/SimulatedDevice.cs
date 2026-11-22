using System.Text;

namespace CalcLink.Core.Simulation;

/// <summary>A variable/app stored in a simulated calculator.</summary>
internal sealed class SimVar
{
    private static long s_nextId;

    public long Id { get; } = Interlocked.Increment(ref s_nextId);
    public required string Folder { get; set; }
    public required string Name { get; set; }
    public required string TypeName { get; init; }
    public required string Extension { get; init; }
    public required byte RawType { get; init; }
    public VariableAttribute Attribute { get; set; }
    public bool IsApp { get; init; }
    public required byte[] Data { get; set; }

    public uint Size => (uint)Data.Length;
    public string FullName => Folder.Length == 0 ? Name : $"{Folder}\\{Name}";
}

/// <summary>Type information for one file extension on one model family.</summary>
internal readonly record struct SimType(string TypeName, byte RawType, bool IsApp = false, bool Executable = false);

/// <summary>Mutable state of one simulated calculator; survives reconnects within a service instance.</summary>
internal sealed class SimDevice
{
    public required SimulatedModel Kind { get; init; }
    public required DeviceDescriptor Descriptor { get; init; }
    public required CalcFeatures Features { get; init; }
    public required string ProductName { get; init; }
    public required string ProductId { get; init; }
    public required string OsVersion { get; set; }
    public required string BootVersion { get; init; }
    public required ulong RamUser { get; init; }
    public required ulong FlashUser { get; init; }
    public required ulong RomSize { get; init; }
    public required int LcdWidth { get; init; }
    public required int LcdHeight { get; init; }
    public required int BitsPerPixel { get; init; }
    public required bool ColorScreen { get; init; }
    public required bool PythonOnBoard { get; init; }
    public required bool ExactMath { get; init; }
    public required int MaxNameLength { get; init; }
    /// <summary>Folder for vars that arrive without one ("" if the model has no folders).</summary>
    public required string DefaultFolder { get; init; }
    public required Func<string, SimType?> TypeForExtension { get; init; }

    public bool HasFolders => (Features & CalcFeatures.Folders) != 0;
    public bool HasClock => (Features & CalcFeatures.Clock) != 0;

    public List<SimVar> Vars { get; } = [];
    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan ClockOffset { get; set; }
    public int TimeFormat { get; set; } = 24;
    public int DateFormat { get; set; } = 1;
    public int KeyPresses { get; set; }

    public Lock Gate { get; } = new();
    public bool InUse { get; set; }
    public long UnpluggedUntilTicks { get; set; }

    public ulong RamUsed => (ulong)Vars.Where(v => !v.IsApp && v.Attribute != VariableAttribute.Archived && !InFlashOnly).Sum(v => (long)v.Size);
    public ulong FlashUsed => (ulong)Vars.Where(v => v.IsApp || v.Attribute == VariableAttribute.Archived || InFlashOnly).Sum(v => (long)v.Size);
    private bool InFlashOnly => Kind == SimulatedModel.NspireCXII;

    public SimVar? Find(string folder, string name) =>
        Vars.FirstOrDefault(v => string.Equals(v.Folder, folder, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(v.Name, name, StringComparison.Ordinal));

    // ------------------------------------------------------------------ catalog

    // Feature masks copied from tilibs libticalcs (calc_84p.cc calc_84pce_usb, calc_nsp.cc calc_nsp,
    // calc_89t.cc calc_89t_usb). The CE lacks IdList/Rename/ChangeAttr there.
    internal const CalcFeatures Ti84PceFeatures =
        CalcFeatures.IsReady | CalcFeatures.Screen | CalcFeatures.DirList | CalcFeatures.Vars | CalcFeatures.Flash |
        CalcFeatures.Os | CalcFeatures.RomDump | CalcFeatures.Clock | CalcFeatures.DeleteVar | CalcFeatures.Version |
        CalcFeatures.Backup | CalcFeatures.Keys |
        CalcFeatures.Silent | CalcFeatures.MemFree | CalcFeatures.FlashApps;

    internal const CalcFeatures NspireFeatures =
        CalcFeatures.IsReady | CalcFeatures.Keys | CalcFeatures.Version | CalcFeatures.Screen | CalcFeatures.IdList |
        CalcFeatures.DirList | CalcFeatures.Vars | CalcFeatures.Os | CalcFeatures.RomDump | CalcFeatures.NewFolder |
        CalcFeatures.DeleteVar | CalcFeatures.Rename |
        CalcFeatures.Silent | CalcFeatures.MemFree | CalcFeatures.Folders;

    internal const CalcFeatures Ti89TFeatures =
        CalcFeatures.IsReady | CalcFeatures.Screen | CalcFeatures.DirList | CalcFeatures.Vars | CalcFeatures.Flash |
        CalcFeatures.Os | CalcFeatures.RomDump | CalcFeatures.IdList | CalcFeatures.Clock | CalcFeatures.DeleteVar |
        CalcFeatures.NewFolder | CalcFeatures.Version | CalcFeatures.Backup | CalcFeatures.Keys | CalcFeatures.Rename |
        CalcFeatures.ChangeAttr |
        CalcFeatures.Silent | CalcFeatures.MemFree | CalcFeatures.FlashApps | CalcFeatures.Folders;

    public static SimDevice Create(SimulatedModel kind, int port) => kind switch
    {
        SimulatedModel.TI84PlusCEPython => CreateCe(port),
        SimulatedModel.NspireCXII => CreateNspire(port),
        SimulatedModel.TI89Titanium => CreateTi89T(port),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static SimDevice CreateCe(int port)
    {
        var d = new SimDevice
        {
            Kind = SimulatedModel.TI84PlusCEPython,
            Descriptor = new DeviceDescriptor("TI-84 Plus CE Python (simulated)", CableModel.DirectLink, port,
                CalcModel.TI84PCE_USB, 0x0451, 0xE008),
            Features = Ti84PceFeatures,
            ProductName = "TI-84 Plus CE",
            ProductId = "134C8E21F3", // USB TI-84 Plus family: product code + 4 bytes (tilibs main_calc_id)
            OsVersion = "5.8.2.0024",
            BootVersion = "5.3.6.0017",
            RamUser = 154_000,
            FlashUser = 3_014_656,
            RomSize = 4 * 1024 * 1024,
            LcdWidth = 320, LcdHeight = 240, BitsPerPixel = 16,
            ColorScreen = true, PythonOnBoard = true, ExactMath = true,
            MaxNameLength = 8,
            DefaultFolder = "",
            TypeForExtension = CeType,
        };

        void Add(string name, string ext, string body, VariableAttribute attr = VariableAttribute.None, int padTo = 0)
        {
            SimType t = CeType(ext)!.Value;
            d.Vars.Add(new SimVar
            {
                Folder = "", Name = name, TypeName = t.TypeName, Extension = ext, RawType = t.RawType,
                IsApp = t.IsApp, Attribute = t.IsApp ? VariableAttribute.Archived : attr,
                Data = SimFiles.Payload(body, padTo),
            });
        }

        Add("QUADFORM", "8xp", "Prompt A,B,C\nB²-4AC→D\nIf D<0\nThen\nDisp \"NO REAL ROOTS\"\nElse\nDisp (-B+√(D))/(2A),(-B-√(D))/(2A)\nEnd");
        Add("FACTOR", "8xp", "Input \"N=\",N\nFor(I,2,√(N))\nWhile not(fPart(N/I))\nDisp I\nN/I→N\nEnd\nEnd\nIf N>1:Disp N", padTo: 211);
        Add("PHYSICS", "8xp", "ClrHome\nMenu(\"PHYSICS\",\"KINEMATICS\",A,\"FORCES\",B,\"ENERGY\",C)", VariableAttribute.Archived, padTo: 2_380);
        Add("TETRIS", "8xp", "ASM", VariableAttribute.Archived, padTo: 9_870);
        Add("CESIUM", "8xp", "ASM shell", VariableAttribute.Archived, padTo: 52_417);
        Add("SNAKE", "8xv", SimFiles.PythonAppVar("import ti_draw as d\nimport ti_system as s\nfrom random import randint\n\ndef main():\n  x, y = 160, 120\n  while not s.escape():\n    d.fill_rect(x, y, 8, 8)\n\nmain()\n"), VariableAttribute.Archived);
        Add("MANDEL", "8xv", SimFiles.PythonAppVar("from ti_draw import *\nfor px in range(320):\n  for py in range(240):\n    c = complex(px/80-2.5, py/80-1.5)\n"), VariableAttribute.Archived);
        Add("TETRISSV", "8xv", "high scores", padTo: 128);
        Add("L₁", "8xl", "list", padTo: 47);
        Add("L₂", "8xl", "list", padTo: 47);
        Add("[A]", "8xm", "matrix", padTo: 85);
        Add("A", "8xn", "real", padTo: 9);
        Add("X", "8xn", "real", padTo: 9);
        Add("Str1", "8xs", "HELLO WORLD");
        Add("Y₁", "8xy", "sin(X)");
        Add("Pic1", "8ci", "picture", VariableAttribute.Archived, padTo: 21_945);
        Add("Image1", "8ca", "image", VariableAttribute.Archived, padTo: 22_247);
        Add("GDB1", "8xd", "graph db", padTo: 342);
        Add("CabriJr", "8ek", "**TIFL** CabriJr", padTo: 84_032);
        Add("Periodic", "8ek", "**TIFL** Periodic", padTo: 61_440);
        Add("Python", "8ek", "**TIFL** Python", padTo: 409_600);
        return d;
    }

    private static SimDevice CreateNspire(int port)
    {
        var d = new SimDevice
        {
            Kind = SimulatedModel.NspireCXII,
            Descriptor = new DeviceDescriptor("TI-Nspire CX II CAS (simulated)", CableModel.DirectLink, port,
                CalcModel.Nspire, 0x0451, 0xE012),
            Features = NspireFeatures,
            ProductName = "TI-Nspire CX II CAS",
            ProductId = "1007D6A3B5D7791F2E3D4C5B6A79", // 28 chars, like tilibs reads from the Nspire
            OsVersion = "6.2.0.333",
            BootVersion = "4.5.0.14",
            RamUser = 64ul * 1024 * 1024,
            FlashUser = 100ul * 1024 * 1024,
            RomSize = 8ul * 1024 * 1024,
            LcdWidth = 320, LcdHeight = 240, BitsPerPixel = 16,
            ColorScreen = true, PythonOnBoard = true, ExactMath = true,
            MaxNameLength = 240,
            DefaultFolder = "",
            TypeForExtension = NspireType,
        };
        foreach (string f in new[] { "Examples", "Physics", "MyLib", "Python" })
            d.Folders.Add(f);

        void Add(string folder, string name, int size)
        {
            d.Vars.Add(new SimVar
            {
                Folder = folder, Name = name, TypeName = "TNS", Extension = "tns", RawType = 0x00,
                Data = SimFiles.Payload("*TIMLP0500" + name, size),
            });
        }

        Add("Examples", "Getting Started", 48_211);
        Add("Examples", "Calculus", 23_870);
        Add("Physics", "Projectile Motion", 15_402);
        Add("Physics", "Kinematics Lab", 31_066);
        Add("MyLib", "numtheory", 4_512);
        Add("Python", "snake.py", 1_834);
        Add("Python", "mandelbrot.py", 922);
        Add("", "Scratchpad", 2_048);
        return d;
    }

    private static SimDevice CreateTi89T(int port)
    {
        var d = new SimDevice
        {
            Kind = SimulatedModel.TI89Titanium,
            Descriptor = new DeviceDescriptor("TI-89 Titanium (simulated)", CableModel.DirectLink, port,
                CalcModel.TI89T_USB, 0x0451, 0xE004),
            Features = Ti89TFeatures,
            ProductName = "TI-89 Titanium",
            ProductId = "091F2E3D4C5B6A", // 14 hex digits (5-5-4 on the About screen)
            OsVersion = "3.10",
            BootVersion = "2.00",
            RamUser = 188_416,
            FlashUser = 2_752_512,
            RomSize = 4 * 1024 * 1024,
            LcdWidth = 160, LcdHeight = 100, BitsPerPixel = 1,
            ColorScreen = false, PythonOnBoard = false, ExactMath = true,
            MaxNameLength = 8,
            DefaultFolder = "main",
            TypeForExtension = Ti89Type,
        };
        foreach (string f in new[] { "main", "games", "physics" })
            d.Folders.Add(f);

        void Add(string folder, string name, string ext, int size, VariableAttribute attr = VariableAttribute.None)
        {
            SimType t = Ti89Type(ext)!.Value;
            d.Vars.Add(new SimVar
            {
                Folder = t.IsApp ? "" : folder, Name = name, TypeName = t.TypeName, Extension = ext, RawType = t.RawType,
                IsApp = t.IsApp, Attribute = t.IsApp ? VariableAttribute.Archived : attr,
                Data = SimFiles.Payload(name, size),
            });
        }

        Add("main", "a", "89e", 12);
        Add("main", "quadf", "89p", 318);
        Add("main", "f", "89f", 41);
        Add("main", "l1", "89l", 64);
        Add("main", "mat1", "89m", 96);
        Add("main", "notes", "89t", 1_204, VariableAttribute.Locked);
        Add("main", "pic1", "89i", 1_968);
        Add("games", "tetris", "89z", 12_455, VariableAttribute.Archived);
        Add("games", "phoenix", "89z", 31_890, VariableAttribute.Archived);
        Add("physics", "proj", "89p", 2_310, VariableAttribute.Archived);
        Add("", "CabriGeo", "89k", 467_000);
        Add("", "EEPro", "89k", 327_680);
        Add("", "StatsLst", "89k", 196_608);
        return d;
    }

    // ------------------------------------------------------------------ type tables

    // TI-84 Plus CE (tilibs types84p.cc). Programs from other 83+/84+ also load (.8x*).
    private static SimType? CeType(string ext) => ext.ToLowerInvariant() switch
    {
        "8xn" => new("REAL", 0x00),
        "8xl" => new("LIST", 0x01),
        "8xm" => new("MAT", 0x02),
        "8xy" => new("EQU", 0x03),
        "8xs" => new("STR", 0x04),
        "8xp" => new("PRGM", 0x05, Executable: true),
        "8xd" => new("GDB", 0x08),
        "8xc" => new("CPLX", 0x0C),
        "8xv" => new("APPV", 0x15),
        "8xg" => new("GRP", 0x17),
        "8ci" => new("PIC", 0x07),
        "8ca" => new("IMAGE", 0x1A),
        "8ek" => new("APP", 0x24, IsApp: true, Executable: true),
        _ => null,
    };

    // TI-Nspire: everything is a document (.tns).
    private static SimType? NspireType(string ext) => ext.ToLowerInvariant() switch
    {
        "tns" => new("TNS", 0x00),
        _ => null,
    };

    // TI-89 Titanium (tilibs types89.cc).
    private static SimType? Ti89Type(string ext) => ext.ToLowerInvariant() switch
    {
        "89e" => new("EXPR", 0x00),
        "89l" => new("LIST", 0x04),
        "89m" => new("MAT", 0x06),
        "89c" => new("DATA", 0x0A),
        "89t" => new("TEXT", 0x0B),
        "89s" => new("STR", 0x0C),
        "89d" => new("GDB", 0x0D),
        "89a" => new("FIG", 0x0E),
        "89i" => new("PIC", 0x10),
        "89p" => new("PRGM", 0x12, Executable: true),
        "89f" => new("FUNC", 0x13, Executable: true),
        "89x" => new("MAC", 0x14),
        "89y" => new("OTH", 0x1C),
        "89z" => new("ASM", 0x21, Executable: true),
        "89k" => new("FLASH", 0x24, IsApp: true, Executable: true),
        _ => null,
    };

    /// <summary>OS upgrade extensions this model accepts.</summary>
    public bool AcceptsOs(string ext) => Kind switch
    {
        SimulatedModel.TI84PlusCEPython => ext is "8eu",
        SimulatedModel.NspireCXII => ext is "tco2" or "tcc2" or "tct2" or "tco" or "tcc" or "tnc" or "tno",
        SimulatedModel.TI89Titanium => ext is "89u",
        _ => false,
    };

    public bool AcceptsCert(string ext) => Kind switch
    {
        SimulatedModel.TI84PlusCEPython => ext is "8eq" or "8xq",
        SimulatedModel.TI89Titanium => ext is "89q",
        _ => false,
    };
}

/// <summary>File-content helpers for the simulator (dummy but correctly structured where cheap).</summary>
internal static class SimFiles
{
    public static byte[] Payload(string text, int padTo = 0)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        if (padTo <= body.Length)
            return body;
        byte[] padded = new byte[padTo];
        body.CopyTo(padded, 0);
        // Deterministic filler so sizes and checksums are stable.
        uint x = 2463534242u ^ (uint)padTo;
        for (int i = body.Length; i < padded.Length; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            padded[i] = (byte)x;
        }
        return padded;
    }

    /// <summary>CE Python AppVars start with "PYCD" (compiled) or "PYSC" (source).</summary>
    public static string PythonAppVar(string source) => "PYCD\0" + source;

    private static readonly byte[] s_ti83fSig = "**TI83F*"u8.ToArray();

    /// <summary>
    /// Writes a TI-83 Plus/84 Plus family variable file ("**TI83F*" container with one or more
    /// variable entries, 13-byte entry header, 16-bit checksum), which is what .8x?/.8c? files use.
    /// </summary>
    public static byte[] WriteTi83F(IReadOnlyList<SimVar> vars, string comment = "Created by CalcLink (simulated)")
    {
        using var data = new MemoryStream();
        var w = new BinaryWriter(data);
        foreach (SimVar v in vars)
        {
            byte[] varData = new byte[v.Data.Length + 2];
            BitConverter.TryWriteBytes(varData.AsSpan(0, 2), (ushort)v.Data.Length);
            v.Data.CopyTo(varData, 2);

            w.Write((ushort)0x0D);
            w.Write((ushort)varData.Length);
            w.Write(v.RawType);
            byte[] name = new byte[8]; // real files hold tokens here; UTF-8 is close enough for a simulator
            byte[] utf8 = Encoding.UTF8.GetBytes(v.Name);
            utf8.AsSpan(0, Math.Min(8, utf8.Length)).CopyTo(name);
            w.Write(name);
            w.Write((byte)0); // version
            w.Write((byte)(v.Attribute == VariableAttribute.Archived ? 0x80 : 0x00));
            w.Write((ushort)varData.Length);
            w.Write(varData);
        }
        w.Flush();
        byte[] section = data.ToArray();

        using var file = new MemoryStream();
        var fw = new BinaryWriter(file);
        fw.Write(s_ti83fSig);
        fw.Write([0x1A, 0x0A, 0x00]);
        byte[] c = new byte[42];
        Encoding.ASCII.GetBytes(comment.Length > 42 ? comment[..42] : comment).CopyTo(c, 0);
        fw.Write(c);
        fw.Write((ushort)section.Length);
        fw.Write(section);
        ushort sum = 0;
        foreach (byte b in section) sum += b;
        fw.Write(sum);
        fw.Flush();
        return file.ToArray();
    }

    /// <summary>Parses the first variable of a "**TI83F*" file: (name, type, archived, body).</summary>
    public static (string Name, byte Type, bool Archived, byte[] Body)? TryReadTi83F(byte[] file)
    {
        // 0x00 signature(8) 0x08 3 bytes 0x0B comment(42) 0x35 data length(2) 0x37 entry:
        // header len(2) var len(2) type(1)@0x3B name(8)@0x3C version@0x44 flag@0x45 var len(2)@0x46 data@0x48
        if (file.Length < 0x4A || !file.AsSpan(0, 8).SequenceEqual(s_ti83fSig))
            return null;
        byte type = file[0x3B];
        var nameSpan = file.AsSpan(0x3C, 8);
        int nul = nameSpan.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(nul >= 0 ? nameSpan[..nul] : nameSpan);
        bool archived = (file[0x45] & 0x80) != 0;
        int len = BitConverter.ToUInt16(file, 0x46);
        if (len < 2 || 0x48 + len > file.Length)
            return null;
        int bodyLen = Math.Min(BitConverter.ToUInt16(file, 0x48), len - 2);
        return (name, type, archived, file.AsSpan(0x4A, bodyLen).ToArray());
    }

    /// <summary>Bytes written for a received variable, in a format matching its extension.</summary>
    public static byte[] Serialize(SimDevice device, SimVar v)
    {
        if (v.IsApp)
            return Join("**TIFL**"u8, Fixed(v.Name, 8), v.Data);
        return device.Kind switch
        {
            SimulatedModel.TI84PlusCEPython => WriteTi83F([v]),
            SimulatedModel.NspireCXII => v.Data.AsSpan().StartsWith("*TIMLP"u8) ? v.Data : Join("*TIMLP0500"u8, v.Data),
            _ => Join("**TI89**"u8, [0x01, 0x00], Fixed(v.Folder, 8), Fixed(v.Name, 8), v.Data),
        };
    }

    private static byte[] Fixed(string s, int length)
    {
        byte[] result = new byte[length];
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        utf8.AsSpan(0, Math.Min(length, utf8.Length)).CopyTo(result);
        return result;
    }

    public static byte[] Join(ReadOnlySpan<byte> head, params byte[][] parts)
    {
        int total = head.Length;
        foreach (byte[] p in parts) total += p.Length;
        byte[] result = new byte[total];
        head.CopyTo(result);
        int o = head.Length;
        foreach (byte[] p in parts) { p.CopyTo(result, o); o += p.Length; }
        return result;
    }

    /// <summary>Extension-based classification, mirroring libtifiles' rules closely enough for the UI.</summary>
    public static TiFileClass Classify(string path, out CalcModel model)
    {
        model = CalcModel.None;
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0)
            return TiFileClass.Unknown;

        switch (ext)
        {
            case "tig":
                return TiFileClass.TiGroup;
            case "tns":
                model = CalcModel.Nspire;
                return TiFileClass.Variable;
            case "tno" or "tnc" or "tco" or "tcc" or "tmo" or "tmc" or "tco2" or "tcc2" or "tct2":
                model = CalcModel.Nspire;
                return TiFileClass.Os;
        }

        if (ext.Length != 3)
            return TiFileClass.Unknown;

        model = ext[..2] switch
        {
            "73" => CalcModel.TI73,
            "82" => CalcModel.TI82,
            "83" => CalcModel.TI83,
            "8x" => CalcModel.TI83P,
            "8c" => CalcModel.TI84PC,
            "8e" => CalcModel.TI84PCE_USB,
            "85" => CalcModel.TI85,
            "86" => CalcModel.TI86,
            "89" => CalcModel.TI89,
            "9x" => CalcModel.TI92P,
            "92" => CalcModel.TI92,
            "v2" => CalcModel.V200,
            _ => CalcModel.None,
        };
        if (model == CalcModel.None)
            return TiFileClass.Unknown;

        return ext[2] switch
        {
            'k' => TiFileClass.App,
            'u' => TiFileClass.Os,
            'q' => TiFileClass.Certificate,
            'b' => TiFileClass.Backup,
            _ => TiFileClass.Variable,
        };
    }
}
