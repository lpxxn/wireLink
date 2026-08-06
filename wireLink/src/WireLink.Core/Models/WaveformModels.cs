namespace WireLink.Core.Models;

/// <summary>录波相别。协议为每个时间段分别提供 A、B、C 三个地址块。</summary>
public enum WaveformPhase
{
    A,
    B,
    C,
}

/// <summary>一个固定 64 点录波读取块的协议元数据。</summary>
public sealed record WaveformBlockDefinition(
    int SegmentIndex,
    WaveformPhase Phase,
    ushort StartAddress,
    ushort Count,
    double SegmentStartMilliseconds)
{
    public ushort EndAddress => checked((ushort)(StartAddress + Count - 1));

    public string TimeRangeText =>
        $"{SegmentStartMilliseconds:0.####}～{SegmentStartMilliseconds + 20:0.####} ms";
}

/// <summary>同一采样时刻对齐后的三相录波点，并保留源寄存器地址。</summary>
public sealed record WaveformPoint(
    int SampleIndex,
    int SegmentIndex,
    int SegmentSampleIndex,
    double TimeMilliseconds,
    short PhaseA,
    short PhaseB,
    short PhaseC,
    ushort PhaseAAddress,
    ushort PhaseBAddress,
    ushort PhaseCAddress);

/// <summary>一次完整的三相录波读取结果。只有 18 个块全部成功时才创建。</summary>
public sealed record WaveformData(
    DateTimeOffset ReadAt,
    double SampleRateHz,
    IReadOnlyList<WaveformPoint> Points,
    double PhaseARms,
    double PhaseBRms,
    double PhaseCRms);

/// <summary>录波读取进度。CompletedBlocks 表示当前块成功后已经完成的数量。</summary>
public sealed record WaveformReadProgress(
    int CompletedBlocks,
    int TotalBlocks,
    WaveformBlockDefinition CurrentBlock);
