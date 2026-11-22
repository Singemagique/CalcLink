// Source-generated P/Invoke bindings for native/include/calclink.h (CL_ABI_VERSION 1),
// exported by calclink_native.dll.
// Keep this file in lock-step with the header: every struct here must match the C layout
// byte-for-byte (see tests/CalcLink.Core.Tests/StructLayoutTests.cs).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CalcLink.Core.Native;

/// <summary>cl_device (80 bytes on x64/ARM64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClDevice
{
    public int cable_model;
    public int cable_port;
    public int calc_model;
    public ushort vid;
    public ushort pid;
    public fixed byte name[64];
}

/// <summary>cl_calc_info (248 bytes on x64/ARM64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClCalcInfo
{
    public uint mask;
    public int calc_model;
    public fixed byte product_name[64];
    public fixed byte product_id[32];
    public fixed byte os_version[32];
    public fixed byte boot_version[32];
    public fixed byte boot2_version[32];
    public ulong ram_user;
    public ulong ram_free;
    public ulong flash_user;
    public ulong flash_free;
    public ushort lcd_width;
    public ushort lcd_height;
    public ushort bits_per_pixel;
    public ushort hw_version;
    public byte battery_ok;
    public byte run_level;
    public byte exact_math;
    public byte clock_support;
    public byte color_screen;
    public byte python_on_board;
    public fixed byte reserved[2];
}

/// <summary>cl_var (48 bytes on x64/ARM64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClVar
{
    public byte* folder;
    public byte* name;
    public byte* type_name;
    public byte* file_ext;
    public uint size;
    public byte type;
    public byte attr;
    public byte is_app;
    public byte reserved;
    public void* opaque;
}

/// <summary>cl_clock (12 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClClock
{
    public ushort year;
    public byte month;
    public byte day;
    public byte hours;
    public byte minutes;
    public byte seconds;
    public byte time_format;
    public byte date_format;
    public fixed byte reserved[3];
}

/// <summary>tilibs InfosMask bits used to decide which <see cref="ClCalcInfo"/> fields are valid.</summary>
[Flags]
internal enum InfosMask : uint
{
    ProductName = 1u << 1,
    HwVersion = 1u << 3,
    BootVersion = 1u << 7,
    OsVersion = 1u << 8,
    RamUser = 1u << 10,
    RamFree = 1u << 11,
    FlashUser = 1u << 13,
    FlashFree = 1u << 14,
    LcdWidth = 1u << 15,
    LcdHeight = 1u << 16,
    BatteryEnough = 1u << 17,
    Boot2Version = 1u << 18,
    RunLevel = 1u << 19,
    Bpp = 1u << 20,
    ProductId = 1u << 22,
    ExactMath = 1u << 23,
    ClockSupport = 1u << 24,
    ColorScreen = 1u << 25,
    PythonOnBoard = 1u << 26,
    CalcModel = 0x80000000,
}

internal static unsafe partial class NativeMethods
{
    public const string LibraryName = "calclink_native"; // calclink_native.dll (CalcLink.dll is the managed app)
    public const int ExpectedAbiVersion = 1;

    // ---- Library ----
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_abi_version();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_init();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void cl_shutdown();

    /// <summary>Returns a static string: do NOT free.</summary>
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* cl_lib_versions();

    /// <summary>Returns a heap string: free with <see cref="cl_free"/>.</summary>
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* cl_error_message(int code);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void cl_free(void* p);

    // ---- Discovery ----
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_probe_usb(ClDevice* @out, int capacity, int* count);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_classify_file(string path, int* file_class, int* calc_model);

    /// <summary>Returns a static string: do NOT free.</summary>
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* cl_model_name(int calc_model);

    // ---- Session ----
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_open(int cable_model, int cable_port, int calc_model, int timeout_ds, nint* @out);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void cl_close(nint s);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint cl_features(nint s);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_calc_model(nint s);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void cl_set_progress(nint s,
        delegate* unmanaged[Cdecl]<void*, int, int, float, byte*, void> fn, void* user);

    /// <summary>Thread-safe: may be called from any thread while the session is open.</summary>
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void cl_cancel(nint s);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_is_ready(nint s);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_get_info(nint s, ClCalcInfo* @out);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_get_memfree(nint s, ulong* ram, ulong* flash);

    // ---- Variables ----
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_get_dirlist(nint s, ClVar** @out, int* count);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void cl_free_dirlist(ClVar* list, int count);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_send_file(nint s, string path);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_recv_var(nint s, ClVar* v, string path);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_delete_var(nint s, ClVar* v);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_rename_var(nint s, ClVar* v, string new_folder, string new_name);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_set_attr(nint s, ClVar* v, int attr);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_new_folder(nint s, string name);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_execute(nint s, ClVar* v);

    // ---- Tools ----
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_send_key(nint s, uint key);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_recv_screen(nint s, byte** pixels, int* width, int* height);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_get_clock(nint s, ClClock* @out);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_set_clock(nint s, ClClock* @in);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_backup(nint s, string path);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int cl_dump_rom(nint s, string path);

    // ---- Helpers ----

    /// <summary>Decodes a NUL-terminated UTF-8 string owned by native code (null → "").</summary>
    public static string FromUtf8(byte* p)
        => p == null ? string.Empty : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(p));

    /// <summary>Decodes a fixed-size char[] field, stopping at the first NUL or at <paramref name="capacity"/>.</summary>
    public static string FromFixedUtf8(byte* p, int capacity)
    {
        var span = new ReadOnlySpan<byte>(p, capacity);
        int nul = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span);
    }

    /// <summary>Text for a native error code (cl_error_message, freed with cl_free).</summary>
    public static string ErrorMessage(int code)
    {
        byte* msg = cl_error_message(code);
        if (msg == null)
            return $"calclink error {code}";
        try
        {
            string text = FromUtf8(msg).Trim();
            return text.Length == 0 ? $"calclink error {code}" : text;
        }
        finally
        {
            cl_free(msg);
        }
    }
}
