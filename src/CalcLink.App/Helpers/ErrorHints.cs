using CalcLink.Core;

namespace CalcLink.App.Helpers;

/// <summary>"What to try" text for common link failures, keyed by <see cref="CalcLinkException.Code"/>.</summary>
public static class ErrorHints
{
    // tilibs codes that CalcErrorCodes doesn't name (values from libticables2 / libticalcs2 error.h).
    private const int CableProbeFailed = 7;          // ERR_PROBE_FAILED
    private const int CableLibusbWin32Missing = 17;  // ERR_LIBUSBWIN32_NOT_PRESENT
    private const int CableLibusbOpen = 37;          // ERR_LIBUSB_OPEN
    private const int CableLibusbClaim = 38;         // ERR_LIBUSB_CLAIM
    private const int CableLibusbConfig = 39;        // ERR_LIBUSB_CONFIG
    private const int CableLibusbReset = 40;         // ERR_LIBUSB_RESET
    private const int CableNoLibrary = 52;           // ERR_NO_LIBRARY
    private const int CalcChecksum = 258;            // ERR_CHECKSUM
    private const int CalcCtsError = 264;            // ERR_CTS_ERROR
    private const int CalcNack = 265;                // ERR_NACK
    private const int CalcInvalidPacket = 266;       // ERR_INVALID_PACKET
    private const int CalcPendingTransfer = 276;     // ERR_PENDING_TRANSFER
    private const int CalcDbusLocked = 353;          // ERR_CALC_ERROR1 + 3: cannot overwrite (locked/archived)
    private const int CalcDusbOutOfMemory = 305;     // ERR_CALC_ERROR2 + 5: out of memory (USB models)
    private const int CalcNspireOutOfMemory = 401;   // ERR_CALC_ERROR3 + 1: out of memory (Nspire)

    private const string NotResponding =
        "Turn the calculator on and leave it on the home screen (quit any program, menu or editor), then try again. " +
        "If it keeps timing out, raise the link timeout in Settings.";

    private const string Busy =
        "The calculator or cable is busy. Wait for any transfer to finish on the calculator, close TI Connect " +
        "and other calculator software, then try again.";

    private const string OutOfMemory =
        "The calculator is out of memory. Delete or archive some variables on it (archived variables don't use RAM), " +
        "then try again.";

    private const string Cable =
        "Check that the cable is pushed fully into the calculator's link port and plugged into the PC, " +
        "then connect again.";

    private const string Driver =
        "CalcLink couldn't open the cable. Close TI Connect and other calculator software, unplug the cable and plug it " +
        "back in, and make sure the WinUSB driver is set up for it (see DRIVERS.md in the CalcLink documentation).";

    public static string? For(Exception ex) =>
        ex is CalcLinkException { Code: var code } and not FeatureNotSupportedException ? For(code) : null;

    public static string? For(int code) => code switch
    {
        CalcErrorCodes.CableReadTimeout or CalcErrorCodes.CableWriteTimeout => NotResponding,
        CalcErrorCodes.CalcNotReady =>
            "The calculator isn't ready. Go to its home screen (quit any program, menu or editor), then try again.",
        CalcErrorCodes.CableBusy or CalcErrorCodes.CalcBusy or CalcPendingTransfer => Busy,
        CalcErrorCodes.CalcOutOfMemory or CalcDusbOutOfMemory or CalcNspireOutOfMemory => OutOfMemory,
        CalcErrorCodes.CalcVarRejected or CalcDbusLocked =>
            "The calculator refused the variable. A variable with that name may be archived or locked, or the " +
            "calculator may be out of memory. Unarchive or delete it on the calculator, or free some memory, then try again.",
        CalcErrorCodes.CalcMissingVar =>
            "The variable isn't on the calculator any more. Refresh the list and try again.",
        CalcErrorCodes.BadFile =>
            "Make sure the file is a TI calculator file for this calculator model (for example .8xp for a TI-83 Plus or TI-84 Plus).",
        CalcChecksum or CalcCtsError or CalcNack or CalcInvalidPacket =>
            "The data was garbled on the way. Push the plug fully into the calculator's link port and try again.",
        CalcErrorCodes.CableNoCable or CalcErrorCodes.CalcNoCable or CableProbeFailed or CableLibusbWin32Missing
            or CableLibusbOpen or CableLibusbClaim or CableLibusbConfig or CableLibusbReset or CableNoLibrary => Driver,
        _ when CalcErrorCodes.IsDisconnect(code) => Cable,
        _ => null,
    };
}
