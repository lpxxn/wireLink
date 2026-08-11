using WireLink.Core.Models;

namespace WireLink.Core.Registers;

/// <summary>
/// 故障录波固定地址目录。采样率由“20 ms 内 64 点”推导，属于实机确认前的暂定规则。
/// </summary>
public static class WaveformCatalog
{
    public const ushort SamplesPerBlock = 64;
    public const int SegmentCount = 6;
    public const int PhaseCount = 3;
    public const int TotalBlocks = SegmentCount * PhaseCount;
    public const int PointsPerPhase = SegmentCount * SamplesPerBlock;
    public const double SampleRateHz = 3200d;
    public const double SampleIntervalMilliseconds = 0.3125d;
    public const double FirstSampleMilliseconds = -80d;

    public static IReadOnlyList<WaveformBlockDefinition> Blocks { get; } =
    [
        Block(0, WaveformPhase.A, 0xB000, -80),
        Block(0, WaveformPhase.B, 0xB040, -80),
        Block(0, WaveformPhase.C, 0xB080, -80),
        Block(1, WaveformPhase.A, 0xB100, -60),
        Block(1, WaveformPhase.B, 0xB140, -60),
        Block(1, WaveformPhase.C, 0xB180, -60),
        Block(2, WaveformPhase.A, 0xB200, -40),
        Block(2, WaveformPhase.B, 0xB240, -40),
        Block(2, WaveformPhase.C, 0xB280, -40),
        Block(3, WaveformPhase.A, 0xB300, -20),
        Block(3, WaveformPhase.B, 0xB340, -20),
        Block(3, WaveformPhase.C, 0xB380, -20),
        Block(4, WaveformPhase.A, 0xB400, 0),
        Block(4, WaveformPhase.B, 0xB440, 0),
        Block(4, WaveformPhase.C, 0xB480, 0),
        Block(5, WaveformPhase.A, 0xB500, 20),
        Block(5, WaveformPhase.B, 0xB540, 20),
        Block(5, WaveformPhase.C, 0xB580, 20),
    ];

    public static WaveformBlockDefinition GetBlock(int segmentIndex, WaveformPhase phase) =>
        Blocks.Single(block => block.SegmentIndex == segmentIndex && block.Phase == phase);

    public static double GetTimeMilliseconds(int sampleIndex)
    {
        if (sampleIndex is < 0 or >= PointsPerPhase)
            throw new ArgumentOutOfRangeException(nameof(sampleIndex));

        return FirstSampleMilliseconds + sampleIndex * SampleIntervalMilliseconds;
    }

    private static WaveformBlockDefinition Block(
        int segmentIndex,
        WaveformPhase phase,
        ushort startAddress,
        double segmentStartMilliseconds) =>
        new(segmentIndex, phase, startAddress, SamplesPerBlock, segmentStartMilliseconds);
}

/// <summary>纯函数形式的录波数值解析和 RMS 计算，供服务与 PDF 回归测试共同使用。</summary>
public static class WaveformSampleDecoder
{
    /// <summary>
    /// Modbus 客户端已按高字节在前还原单寄存器；这里只按二进制补码转换为 int16，不交换字节。
    /// </summary>
    public static short DecodeSigned(ushort value) => unchecked((short)value);

    public static short[] DecodeSignedSamples(IEnumerable<ushort> values) =>
        values.Select(DecodeSigned).ToArray();

    public static double CalculateRms(IEnumerable<short> values)
    {
        var samples = values as IReadOnlyCollection<short> ?? values.ToArray();
        if (samples.Count == 0) return 0;

        var sumOfSquares = samples.Sum(value => (double)value * value);
        return Math.Sqrt(sumOfSquares / samples.Count);
    }
}
