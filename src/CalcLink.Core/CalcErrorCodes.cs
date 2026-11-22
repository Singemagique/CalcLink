namespace CalcLink.Core;

/// <summary>
/// Well-known values of <see cref="CalcLinkException.Code"/>. Negative codes are calclink shim errors
/// (CL_E_* in native/include/calclink.h); positive codes come from tilibs (libticables2 &lt; 256,
/// libticalcs2 &gt;= 256, see tilibs error.h).
/// </summary>
public static class CalcErrorCodes
{
    public const int Ok = 0;

    // ---- calclink shim (CL_E_*) ----
    public const int InvalidArgument = -1;
    public const int NoMemory = -2;
    public const int NotSupported = -3;
    public const int NotOpen = -4;
    public const int Cancelled = -5;
    public const int BadFile = -6;
    public const int Internal = -7;

    // ---- libticables2 (CableError) ----
    public const int CableBusy = 1;
    public const int CableReadError = 3;
    public const int CableReadTimeout = 4;
    public const int CableWriteError = 5;
    public const int CableWriteTimeout = 6;
    public const int CableNoCable = 50;
    public const int CableNotOpen = 51;

    // ---- libticalcs2 (CalcError) ----
    public const int CalcAbort = 256;
    public const int CalcNotReady = 257;
    public const int CalcVarRejected = 263;
    public const int CalcNoCable = 268;
    public const int CalcBusy = 269;
    public const int CalcMissingVar = 278;
    public const int CalcOutOfMemory = 273;

    /// <summary>
    /// Codes that we treat as "the device went away" (cable unplugged, calculator reset, USB device
    /// removed). Deliberately conservative: timeouts (4, 6) are NOT included because a busy or
    /// sleeping calculator also times out. libticables maps libusb's NO_DEVICE/IO errors to
    /// ERR_READ_ERROR / ERR_WRITE_ERROR, which is why 3 and 5 are here. Unverified on real hardware.
    /// </summary>
    public static bool IsDisconnect(int code) => code is
        CableReadError or CableWriteError or CableNoCable or CableNotOpen or CalcNoCable or NotOpen;
}
