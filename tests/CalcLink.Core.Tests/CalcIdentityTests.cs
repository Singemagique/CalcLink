using CalcLink.Core;

namespace CalcLink.Core.Tests;

public class CalcIdentityTests
{
    [Fact]
    public void Fourteen_digit_id_is_grouped_like_the_about_screen()
        => Assert.Equal("04012-3AB4C-D5E6", CalcIdentity.FormatId("040123ab4cd5e6"));

    [Theory]
    [InlineData("0a1b2c3d4e", "0A1B2C3D4E")] // TI-84 Plus over USB: 10 digits, not grouped
    [InlineData("  0a1b2c3d4e ", "0A1B2C3D4E")]
    [InlineData("1007D6A3B5D7791F2E3D4C5B6A79", "1007D6A3B5D7791F2E3D4C5B6A79")]
    [InlineData("ABC-123", "ABC-123")] // not hex: unchanged
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Other_ids_are_uppercased_hex_or_left_alone(string? id, string expected)
        => Assert.Equal(expected, CalcIdentity.FormatId(id));

    [Theory]
    [InlineData(CalcModel.TI83P, "040123ab4cd5e6", "04")]
    [InlineData(CalcModel.TI84P, "0a0123ab4cd5e6", "0A")]
    [InlineData(CalcModel.TI84PCE_USB, "134C8E21F3", "13")]
    public void Product_code_is_the_first_byte_of_a_z80_id(CalcModel model, string id, string expected)
        => Assert.Equal(expected, CalcIdentity.ProductCode(model, id));

    [Theory]
    [InlineData(CalcModel.TI89T_USB, "091F2E3D4C5B6A")] // 68k: not known to start with the product code
    [InlineData(CalcModel.Nspire, "1007D6A3B5D7791F2E3D4C5B6A79")]
    [InlineData(CalcModel.TI83P, "")]
    [InlineData(CalcModel.TI83P, "0401")] // too short to be an ID
    [InlineData(CalcModel.TI83P, "zz0123ab4cd5e6")]
    public void Product_code_is_null_when_unknown(CalcModel model, string id)
        => Assert.Null(CalcIdentity.ProductCode(model, id));

    [Fact]
    public async Task Simulator_reports_a_calc_id()
    {
        var service = new CalcLink.Core.Simulation.SimulatedCalculatorService(CalcLink.Core.Simulation.SimulatorOptions.Instant);
        var devices = await service.ProbeAsync();
        await using var session = await service.ConnectAsync(devices[0]);
        CalcInfo info = await session.GetInfoAsync();
        Assert.False(string.IsNullOrEmpty(info.CalcId));
        Assert.Equal(info.ProductId, info.CalcId);
    }
}
