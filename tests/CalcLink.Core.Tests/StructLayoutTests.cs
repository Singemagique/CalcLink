using CalcLink.Core.Native;

namespace CalcLink.Core.Tests;

/// <summary>
/// The C# interop structs must match native/include/calclink.h byte-for-byte. Expected values are
/// computed by hand for x64 MSVC (ARM64 MSVC is identical: same natural alignment, 8-byte pointers):
/// every field is aligned to its own size, char arrays to 1, and the struct size is rounded up to the
/// largest member alignment.
/// </summary>
public unsafe class StructLayoutTests
{
    private static long Offset<T>(T* basePtr, void* field) where T : unmanaged => (byte*)field - (byte*)basePtr;

    [Fact]
    public void PointersAre64Bit()
    {
        // The app ships x64 and ARM64 only; the offsets below assume 8-byte pointers.
        Assert.Equal(8, sizeof(nint));
    }

    [Fact]
    public void ClDevice_MatchesHeader()
    {
        // int32 cable_model  @0  (4)
        // int32 cable_port   @4  (4)
        // int32 calc_model   @8  (4)
        // uint16 vid         @12 (2)
        // uint16 pid         @14 (2)
        // char name[64]      @16 (64)  -> ends at 80
        // max alignment 4: 80 is a multiple of 4 -> sizeof = 80
        Assert.Equal(80, sizeof(ClDevice));
        ClDevice d;
        ClDevice* p = &d;
        Assert.Equal(0, Offset(p, &p->cable_model));
        Assert.Equal(4, Offset(p, &p->cable_port));
        Assert.Equal(8, Offset(p, &p->calc_model));
        Assert.Equal(12, Offset(p, &p->vid));
        Assert.Equal(14, Offset(p, &p->pid));
        Assert.Equal(16, Offset(p, p->name));
    }

    [Fact]
    public void ClCalcInfo_MatchesHeader()
    {
        // uint32 mask             @0   (4)
        // int32  calc_model       @4   (4)
        // char product_name[64]   @8   -> 72
        // char product_id[32]     @72  -> 104
        // char os_version[32]     @104 -> 136
        // char boot_version[32]   @136 -> 168
        // char boot2_version[32]  @168 -> 200
        // uint64 ram_user         @200 (200 % 8 == 0, no padding) -> 208
        // uint64 ram_free         @208 -> 216
        // uint64 flash_user       @216 -> 224
        // uint64 flash_free       @224 -> 232
        // uint16 lcd_width        @232, lcd_height @234, bits_per_pixel @236, hw_version @238 -> 240
        // uint8 battery_ok @240, run_level @241, exact_math @242, clock_support @243,
        //       color_screen @244, python_on_board @245, reserved[2] @246 -> 248
        // max alignment 8: 248 = 31 * 8 -> sizeof = 248
        Assert.Equal(248, sizeof(ClCalcInfo));
        ClCalcInfo i;
        ClCalcInfo* p = &i;
        Assert.Equal(0, Offset(p, &p->mask));
        Assert.Equal(4, Offset(p, &p->calc_model));
        Assert.Equal(8, Offset(p, p->product_name));
        Assert.Equal(72, Offset(p, p->product_id));
        Assert.Equal(104, Offset(p, p->os_version));
        Assert.Equal(136, Offset(p, p->boot_version));
        Assert.Equal(168, Offset(p, p->boot2_version));
        Assert.Equal(200, Offset(p, &p->ram_user));
        Assert.Equal(208, Offset(p, &p->ram_free));
        Assert.Equal(216, Offset(p, &p->flash_user));
        Assert.Equal(224, Offset(p, &p->flash_free));
        Assert.Equal(232, Offset(p, &p->lcd_width));
        Assert.Equal(234, Offset(p, &p->lcd_height));
        Assert.Equal(236, Offset(p, &p->bits_per_pixel));
        Assert.Equal(238, Offset(p, &p->hw_version));
        Assert.Equal(240, Offset(p, &p->battery_ok));
        Assert.Equal(241, Offset(p, &p->run_level));
        Assert.Equal(242, Offset(p, &p->exact_math));
        Assert.Equal(243, Offset(p, &p->clock_support));
        Assert.Equal(244, Offset(p, &p->color_screen));
        Assert.Equal(245, Offset(p, &p->python_on_board));
        Assert.Equal(246, Offset(p, p->reserved));
    }

    [Fact]
    public void ClVar_MatchesHeader()
    {
        // const char* folder     @0  (8)
        // const char* name       @8  (8)
        // const char* type_name  @16 (8)
        // const char* file_ext   @24 (8)
        // uint32 size            @32 (4)
        // uint8 type @36, attr @37, is_app @38, reserved @39  -> 40
        // void* opaque           @40 (8, 40 % 8 == 0) -> 48
        // max alignment 8: sizeof = 48
        Assert.Equal(48, sizeof(ClVar));
        ClVar v;
        ClVar* p = &v;
        Assert.Equal(0, Offset(p, &p->folder));
        Assert.Equal(8, Offset(p, &p->name));
        Assert.Equal(16, Offset(p, &p->type_name));
        Assert.Equal(24, Offset(p, &p->file_ext));
        Assert.Equal(32, Offset(p, &p->size));
        Assert.Equal(36, Offset(p, &p->type));
        Assert.Equal(37, Offset(p, &p->attr));
        Assert.Equal(38, Offset(p, &p->is_app));
        Assert.Equal(39, Offset(p, &p->reserved));
        Assert.Equal(40, Offset(p, &p->opaque));
    }

    [Fact]
    public void ClClock_MatchesHeader()
    {
        // uint16 year       @0 (2)
        // uint8 month @2, day @3, hours @4, minutes @5, seconds @6, time_format @7, date_format @8
        // uint8 reserved[3] @9 -> 12
        // max alignment 2: 12 is even -> sizeof = 12
        Assert.Equal(12, sizeof(ClClock));
        ClClock c;
        ClClock* p = &c;
        Assert.Equal(0, Offset(p, &p->year));
        Assert.Equal(2, Offset(p, &p->month));
        Assert.Equal(3, Offset(p, &p->day));
        Assert.Equal(4, Offset(p, &p->hours));
        Assert.Equal(5, Offset(p, &p->minutes));
        Assert.Equal(6, Offset(p, &p->seconds));
        Assert.Equal(7, Offset(p, &p->time_format));
        Assert.Equal(8, Offset(p, &p->date_format));
        Assert.Equal(9, Offset(p, p->reserved));
    }

    [Fact]
    public void FixedStringDecoding_StopsAtNulAndCapacity()
    {
        ClDevice d = default;
        byte* name = d.name;
        "TI-84 Plus CE"u8.CopyTo(new Span<byte>(name, 64));
        Assert.Equal("TI-84 Plus CE", NativeMethods.FromFixedUtf8(name, 64));

        new Span<byte>(name, 64).Fill((byte)'A'); // no terminator at all
        Assert.Equal(new string('A', 64), NativeMethods.FromFixedUtf8(name, 64));
    }

    [Fact]
    public void CalcInfoMapping_RespectsMask()
    {
        ClCalcInfo info = default;
        info.mask = (uint)(InfosMask.ProductName | InfosMask.OsVersion | InfosMask.ColorScreen | InfosMask.RunLevel);
        "TI-84 Plus CE"u8.CopyTo(new Span<byte>(info.product_name, 64));
        "5.8.2.0024"u8.CopyTo(new Span<byte>(info.os_version, 32));
        info.color_screen = 1;
        info.python_on_board = 1; // not in mask -> unknown
        info.run_level = 2;
        info.lcd_width = 320;

        CalcInfo ci = NativeCalculatorSession.ToCalcInfo(&info, CalcModel.TI84PCE_USB);
        Assert.Equal(CalcModel.TI84PCE_USB, ci.Model);
        Assert.Equal("TI-84 Plus CE", ci.ProductName);
        Assert.Equal("5.8.2.0024", ci.OsVersion);
        Assert.True(ci.ColorScreen);
        Assert.Null(ci.PythonOnBoard);
        Assert.Null(ci.BatteryOk);
        Assert.Equal(2, ci.RunLevel);
        Assert.Equal(320, ci.LcdWidth);
    }
}
