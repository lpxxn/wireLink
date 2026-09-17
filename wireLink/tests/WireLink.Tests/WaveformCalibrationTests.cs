using WireLink.Core.Models;

namespace WireLink.Tests;

public sealed class WaveformCalibrationTests
{
    [Theory]
    [InlineData(0xF004,0,"框I",1.0)]
    [InlineData(0xF104,1,"框II",1.5)]
    [InlineData(0xF204,2,"框III",2.0)]
    public void Frame_level_uses_only_bits_8_through_11(
        int registerValue,
        int expectedLevel,
        string expectedFrameName,
        double expectedRate)
    {
        var calibration=WaveformCalibration.FromRegisterValue((ushort)registerValue);

        Assert.Equal((byte)expectedLevel,calibration.FrameLevel);
        Assert.Equal(expectedFrameName,calibration.FrameName);
        Assert.Equal(expectedRate,calibration.Rate);
        Assert.Equal((ushort)registerValue,calibration.RegisterValue);
    }

    [Theory]
    [InlineData(0x0304)]
    [InlineData(0x0F04)]
    public void Unknown_frame_level_is_rejected(int registerValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            ()=>WaveformCalibration.FromRegisterValue((ushort)registerValue));
    }

    [Theory]
    [InlineData(0x0000,10000.0)]
    [InlineData(0x0100,15000.0)]
    [InlineData(0x0200,20000.0)]
    public void Reference_ad_value_maps_to_reference_current_times_rate(int registerValue,double expectedAmperes)
    {
        var calibration=WaveformCalibration.FromRegisterValue((ushort)registerValue);

        Assert.Equal(expectedAmperes,calibration.ConvertToAmperes(22953),10);
        Assert.Equal(-expectedAmperes,calibration.ConvertToAmperes(-22953),10);
    }

    [Fact]
    public void Ffff_is_decoded_as_signed_minus_one_before_ampere_conversion()
    {
        var calibration=WaveformCalibration.FromRegisterValue(0x0204);
        var signedAd=WireLink.Core.Registers.WaveformSampleDecoder.DecodeSigned(0xFFFF);

        Assert.Equal(-1,signedAd);
        Assert.Equal(-0.9,calibration.ConvertToAmperes(signedAd));
        Assert.NotEqual(65535 * 20000.0 / 22953.0,calibration.ConvertToAmperes(signedAd));
    }

    [Theory]
    [InlineData(1,0.9)]
    [InlineData(3,2.6)]
    [InlineData(-3,-2.6)]
    public void Converted_ampere_value_is_rounded_to_one_decimal(short signedAd,double expected)
    {
        var calibration=WaveformCalibration.FromRegisterValue(0x0204);

        Assert.Equal(expected,calibration.ConvertToAmperes(signedAd));
    }
}
