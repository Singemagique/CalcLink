using CalcLink.Core;
using Xunit;

namespace CalcLink.Core.Tests;

public class UsbDriversTests
{
    [Fact]
    public void ParseList_reads_tab_separated_lines_and_skips_junk()
    {
        string output =
            "USB\\VID_0451&PID_E001\tTI-GRAPH LINK USB\tWinUSB\t0\r\n" +
            "USB\\VID_0451&PID_E008\tTI-84 Plus CE\tTiUSB\t0\n" +
            "not a device line\n" +
            "USB\\VID_0451&PID_E003\tTI-84 Plus\t\t28\n" +
            "USB\\VID_0451&PID_E004\ttoo\tfew\n" +
            "\n";

        var devices = UsbDrivers.ParseList(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(new UsbDriverDevice("USB\\VID_0451&PID_E001", "TI-GRAPH LINK USB", "WinUSB", 0), devices[0]);
        Assert.Equal("TiUSB", devices[1].Service);
        Assert.Equal(28, devices[2].ProblemCode);
    }

    [Theory]
    [InlineData("WinUSB", 0, true)]
    [InlineData("winusb", 0, true)]
    [InlineData("libusbK", 0, true)]
    [InlineData("libusb0", 0, true)]
    [InlineData("WinUSB", 10, false)]  // driver assigned but the device failed to start
    [InlineData("TiUSB", 0, false)]
    [InlineData("", 28, false)]
    public void HasUsableDriver(string service, int problem, bool usable) =>
        Assert.Equal(usable, new UsbDriverDevice("USB\\VID_0451&PID_E001", "x", service, problem).HasUsableDriver);

    [Theory]
    [InlineData("WinUSB", 0, "WinUSB")]
    [InlineData("", 28, "no driver")]
    [InlineData("TiUSB", 0, "another driver (TiUSB), probably TI Connect's")]
    public void DriverText(string service, int problem, string expected) =>
        Assert.Equal(expected, new UsbDriverDevice("USB\\VID_0451&PID_E001", "x", service, problem).DriverText);
}
