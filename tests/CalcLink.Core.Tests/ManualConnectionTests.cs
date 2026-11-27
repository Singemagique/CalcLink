using CalcLink.Core.Simulation;

namespace CalcLink.Core.Tests;

/// <summary>GrayLink / hand-picked connections, and leaving exam mode.</summary>
public class ManualConnectionTests
{
    [Theory]
    [InlineData("COM1", "COM1")]
    [InlineData("com7", "COM7")]
    [InlineData(" COM12 ", "COM12")]
    [InlineData(@"\\.\COM3", "COM3")]
    [InlineData("COM255", "COM255")]
    [InlineData("COM007", "COM7")]
    public void SerialPorts_Normalize_AcceptsComPorts(string input, string expected)
        => Assert.Equal(expected, SerialPorts.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("COM")]
    [InlineData("COM0")]
    [InlineData("COM256")]
    [InlineData("COM1234")]
    [InlineData("LPT1")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("COM1;COM2")]
    public void SerialPorts_Normalize_RejectsEverythingElse(string? input)
        => Assert.Null(SerialPorts.Normalize(input));

    [Fact]
    public void SerialPorts_GetNames_AreNormalizedAndSorted()
    {
        IReadOnlyList<string> names = SerialPorts.GetNames();
        Assert.All(names, n => Assert.Equal(n, SerialPorts.Normalize(n)));
        Assert.Equal(names.OrderBy(SerialPorts.Number), names);
    }

    [Fact]
    public void DeviceDescriptor_SerialPortTakesPartInEquality()
    {
        var a = new DeviceDescriptor("GrayLink", CableModel.GrayLink, 1, CalcModel.TI83P, 0, 0) { SerialPort = "COM3" };
        var b = a with { SerialPort = "COM4" };
        Assert.NotEqual(a, b);
        Assert.Equal(a, b with { SerialPort = "COM3" });
    }

    [Fact]
    public async Task Simulator_GrayLink_HasAPortButNoCalculator()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant);
        string port = Assert.Single(svc.GetSerialPorts());
        var gray = new DeviceDescriptor("GrayLink", CableModel.GrayLink, 1, CalcModel.None, 0, 0) { SerialPort = port };

        Assert.Equal(CalcModel.None, await svc.DetectModelAsync(gray));
        var ex = await Assert.ThrowsAsync<CalcLinkException>(() => svc.ConnectAsync(gray with { Model = CalcModel.TI83P }));
        Assert.Equal(CalcErrorCodes.CableReadTimeout, ex.Code);
    }

    [Fact]
    public async Task Simulator_DetectModel_ReturnsTheUsbModel()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant);
        DeviceDescriptor ce = (await svc.ProbeAsync()).Single(d => d.Model == CalcModel.TI84PCE_USB);
        Assert.Equal(CalcModel.TI84PCE_USB, await svc.DetectModelAsync(ce));
    }

    [Fact]
    public async Task LeaveExamMode_CeKeepsTheLink()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant);
        DeviceDescriptor ce = (await svc.ProbeAsync()).Single(d => d.Model == CalcModel.TI84PCE_USB);
        await using var session = await svc.ConnectAsync(ce);

        Assert.True(session.CanLeaveExamMode);
        await session.LeaveExamModeAsync();
        Assert.Equal(1, ((SimulatedCalculatorSession)session).ExamExits);
        Assert.True(await session.IsReadyAsync()); // still connected
    }

    [Fact]
    public async Task LeaveExamMode_NspireRestartsAndDropsTheLink()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant);
        DeviceDescriptor nspire = (await svc.ProbeAsync()).Single(d => d.Model == CalcModel.Nspire);
        await using var session = await svc.ConnectAsync(nspire);
        var lost = new TaskCompletionSource<CalcLinkException>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, e) => lost.TrySetResult(e);

        Assert.True(session.CanLeaveExamMode);
        await session.LeaveExamModeAsync();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LeaveExamMode_UnsupportedModel_Throws()
    {
        var svc = new SimulatedCalculatorService(SimulatorOptions.Instant);
        DeviceDescriptor ti89 = (await svc.ProbeAsync()).Single(d => d.Model == CalcModel.TI89T_USB);
        await using var session = await svc.ConnectAsync(ti89);

        Assert.False(session.CanLeaveExamMode);
        var ex = await Assert.ThrowsAsync<CalcLinkException>(() => session.LeaveExamModeAsync());
        Assert.Equal(CalcErrorCodes.NotSupported, ex.Code);
    }
}
