using System.Buffers.Binary;
using WireLink.Core.Models;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;
using Xunit.Abstractions;

namespace WireLink.Tests;

/// <summary>使用《故障录波协议说明.pdf》截图中的完整响应帧锁定端序、符号位和采样结果。</summary>
public sealed class WaveformPdfSampleTests(ITestOutputHelper output)
{
    private static readonly short[] ExpectedB200 =
    [
        6704, 6704, 6720, 6704, 6704, 6720, 6720, 6720,
        6720, 6704, 6720, 6704, 6672, 6240, 6048, 5616,
        5200, 4752, 4336, 3952, 3568, 3168, 2784, 2416,
        2032, 1664, 1328, 992, 624, 304, 0, -320,
        -624, -912, -1200, -1488, -1760, -2032, -2320, -2576,
        -2848, -3104, -3344, -3584, -3824, -4064, -4304, -4544,
        -4752, -4976, -5168, -5360, -5568, -5760, -5968, -6160,
        -6336, -6512, -6704, -6880, -7024, -7184, -7376, -7536,
    ];

    private static readonly short[] ExpectedB580 =
    [
        1, 2, 0, 1, 0, 1, 1, 0,
        1, 0, 0, 0, 0, 1, 1, 0,
        0, 0, 0, 0, 0, 1, 1, 1,
        1, -1, -1, -1, -1, -1, 0, 1,
        1, 1, 0, 0, 1, 1, 0, -1,
        0, -1, 0, 0, 0, 1, -1, -1,
        -1, 0, 0, 0, 1, 0, 1, 0,
        1, -1, 0, 1, 2, 0, 0, 0,
    ];

    [Fact]
    public void Pdf_B200_response_has_valid_crc()
    {
        var frame = B200Frame();

        Assert.Equal(133, frame.Length);
        Assert.Equal(0x80, frame[2]);
        Assert.Equal(0x5D, frame[^2]);
        Assert.Equal(0x6E, frame[^1]);
        Assert.True(Crc16Modbus.IsValid(frame));
    }

    [Fact]
    public void Pdf_B200_decodes_as_big_endian_signed_samples()
    {
        var samples = DecodeResponse(B200Frame());

        Assert.Equal(ExpectedB200, samples);
        Assert.Equal(64, samples.Length);
        Assert.Equal(6704, samples[0]);
        Assert.Equal(0, samples[30]);
        Assert.Equal(-320, samples[31]);
        Assert.Equal(-7536, samples[^1]);
        var rms = WaveformSampleDecoder.CalculateRms(samples);
        Assert.Equal(4978.873768233133, rms, 9);
        WriteSummary("B200H A相 -40～-20ms", samples, rms);
    }

    [Fact]
    public void Pdf_B580_response_has_valid_crc()
    {
        var frame = B580Frame();

        Assert.Equal(133, frame.Length);
        Assert.Equal(0x80, frame[2]);
        Assert.Equal(0x29, frame[^2]);
        Assert.Equal(0x93, frame[^1]);
        Assert.True(Crc16Modbus.IsValid(frame));
    }

    [Fact]
    public void Pdf_B580_decodes_64_near_zero_samples()
    {
        var samples = DecodeResponse(B580Frame());

        Assert.Equal(ExpectedB580, samples);
        Assert.Equal(-1, samples.Min());
        Assert.Equal(2, samples.Max());
        var rms = WaveformSampleDecoder.CalculateRms(samples);
        Assert.Equal(0.7905694150420949, rms, 12);
        WriteSummary("B580H C相 20～40ms", samples, rms);
    }

    [Fact]
    public void Pdf_samples_must_not_be_byte_swapped()
    {
        Assert.Equal(6704, WaveformSampleDecoder.DecodeSigned(0x1A30));
        Assert.Equal(-320, WaveformSampleDecoder.DecodeSigned(0xFEC0));
        Assert.NotEqual(12314, WaveformSampleDecoder.DecodeSigned(0x1A30));
        Assert.NotEqual(-16130, WaveformSampleDecoder.DecodeSigned(0xFEC0));
    }

    [Fact]
    public void Pdf_sample_time_axis_is_generated_from_segment_start()
    {
        Assert.Equal(-40, WaveformCatalog.GetBlock(2, WaveformPhase.A).SegmentStartMilliseconds);
        Assert.Equal(-40, WaveformCatalog.GetTimeMilliseconds(128));
        Assert.Equal(-20.3125, WaveformCatalog.GetTimeMilliseconds(191));
        Assert.Equal(20, WaveformCatalog.GetTimeMilliseconds(320));
        Assert.Equal(39.6875, WaveformCatalog.GetTimeMilliseconds(383));
    }

    private void WriteSummary(string name, IReadOnlyList<short> samples, double rms) =>
        output.WriteLine(
            $"{name}: 点数={samples.Count}, 首值={samples[0]}, 末值={samples[^1]}, " +
            $"范围={samples.Min()}～{samples.Max()} AD, RMS={rms:F6} AD");

    private static short[] DecodeResponse(byte[] frame)
    {
        Assert.True(Crc16Modbus.IsValid(frame));
        Assert.Equal(0x04, frame[0]);
        Assert.Equal(0x03, frame[1]);
        Assert.Equal(0x80, frame[2]);

        return Enumerable.Range(0, 64)
            .Select(index => WaveformSampleDecoder.DecodeSigned(
                BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3 + index * 2, 2))))
            .ToArray();
    }

    private static byte[] B200Frame() => FromHex("""
        04 03 80
        1A 30 1A 30 1A 40 1A 30 1A 30 1A 40 1A 40 1A 40 1A 40
        1A 30 1A 40 1A 30 1A 10 18 60 17 A0 15 F0 14 50 12 90 10 F0 0F
        70 0D F0 0C 60 0A E0 09 70 07 F0 06 80 05 30 03 E0 02 70 01 30
        00 00 FE C0 FD 90 FC 70 FB 50 FA 30 F9 20 F8 10 F6 F0 F5 F0 F4
        E0 F3 E0 F2 F0 F2 00 F1 10 F0 20 EF 30 EE 40 ED 70 EC 90 EB D0
        EB 10 EA 40 E9 80 E8 B0 E7 F0 E7 40 E6 90 E5 D0 E5 20 E4 90 E3
        F0 E3 30 E2 90 5D 6E
        """);

    private static byte[] B580Frame() => FromHex("""
        04 03 80
        00 01 00 02 00 00 00 01 00 00 00 01 00 01 00 00
        00 01 00 00 00 00 00 00 00 00 00 01 00 01 00 00
        00 00 00 00 00 00 00 00 00 00 00 01 00 01 00 01
        00 01 FF FF FF FF FF FF FF FF FF FF 00 00 00 01
        00 01 00 01 00 00 00 00 00 01 00 01 00 00 FF FF
        00 00 FF FF 00 00 00 00 00 00 00 01 FF FF FF FF
        FF FF 00 00 00 00 00 00 00 01 00 00 00 01 00 00
        00 01 FF FF 00 00 00 01 00 02 00 00 00 00 00 00
        29 93
        """);

    private static byte[] FromHex(string value) =>
        Convert.FromHexString(string.Concat(value.Where(character => !char.IsWhiteSpace(character))));
}
