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

/// <summary>
/// 录波 AD 值到安培的标定信息。框架等级来自寄存器 1552 的 bit8～bit11；
/// bit0～bit7 的额定电流序值和 bit12～bit15 的保留位均不参与本换算。
/// </summary>
public sealed record WaveformCalibration
{
    /// <summary>厂商公式中的基准 AD 值。</summary>
    public const double ReferenceAdValue = 22953.0;

    /// <summary>厂商公式中的基准电流，单位 A。</summary>
    public const double ReferenceCurrentAmperes = 10000.0;

    private WaveformCalibration(ushort registerValue, byte frameLevel, double rate)
    {
        RegisterValue = registerValue;
        FrameLevel = frameLevel;
        Rate = rate;
    }

    /// <summary>寄存器 1552 的完整 16 位原值，供日志、明细和导出追溯。</summary>
    public ushort RegisterValue { get; }

    /// <summary>从寄存器 1552 的 bit8～bit11 提取出的框架等级原值（0、1、2）。</summary>
    public byte FrameLevel { get; }

    /// <summary>
    /// 框架等级在界面中的中文名称：0→框I，1→框II，2→框III。
    /// 数字原值仍由 <see cref="FrameLevel"/> 保存，便于协议核查和 Excel 追溯。
    /// </summary>
    public string FrameName => FrameLevel switch
    {
        0 => "框I",
        1 => "框II",
        2 => "框III",
        _ => throw new InvalidOperationException($"不受支持的框架等级：{FrameLevel}。"),
    };

    /// <summary>框架等级对应倍率：框I→1，框II→1.5，框III→2。</summary>
    public double Rate { get; }

    /// <summary>一个有符号 AD 单位对应的安培数。</summary>
    public double AmperesPerAd => ReferenceCurrentAmperes / ReferenceAdValue * Rate;

    /// <summary>
    /// 根据寄存器 1552 原值创建标定。未知框架等级不能猜测倍率，因此直接拒绝本次录波。
    /// </summary>
    public static WaveformCalibration FromRegisterValue(ushort registerValue)
    {
        var frameLevel = (byte)((registerValue >> 8) & 0x0F);
        var rate = frameLevel switch
        {
            0 => 1.0,
            1 => 1.5,
            2 => 2.0,
            _ => throw new ArgumentOutOfRangeException(
                nameof(registerValue),
                registerValue,
                $"寄存器 1552 的框架等级 {frameLevel} 不受支持，只允许 0、1、2。"),
        };

        return new WaveformCalibration(registerValue, frameLevel, rate);
    }

    /// <summary>
    /// 将已经按大端解码并转成 <see cref="short"/> 的有符号 AD 值换算为安培。
    /// 输入为负数时结果仍为负数；最终安培值按通常四舍五入保留 1 位小数。
    /// </summary>
    public double ConvertToAmperes(short signedAdValue) =>
        RoundAmperes(signedAdValue * AmperesPerAd);

    /// <summary>将安培结果按通常四舍五入保留 1 位小数。</summary>
    public static double RoundAmperes(double amperes) =>
        Math.Round(amperes, 1, MidpointRounding.AwayFromZero);
}

/// <summary>一次完整的三相录波读取结果。只有标定参数和 18 个块全部成功时才创建。</summary>
public sealed record WaveformData(
    DateTimeOffset ReadAt,
    double SampleRateHz,
    IReadOnlyList<WaveformPoint> Points,
    double PhaseARms,
    double PhaseBRms,
    double PhaseCRms,
    WaveformCalibration Calibration)
{
    /// <summary>A 相安培 RMS；线性倍率下等于 AD-RMS 乘每 AD 安培系数。</summary>
    public double PhaseAAmperesRms => WaveformCalibration.RoundAmperes(
        PhaseARms * Calibration.AmperesPerAd);

    /// <summary>B 相安培 RMS。</summary>
    public double PhaseBAmperesRms => WaveformCalibration.RoundAmperes(
        PhaseBRms * Calibration.AmperesPerAd);

    /// <summary>C 相安培 RMS。</summary>
    public double PhaseCAmperesRms => WaveformCalibration.RoundAmperes(
        PhaseCRms * Calibration.AmperesPerAd);
}

/// <summary>录波读取进度。CompletedBlocks 表示当前块成功后已经完成的数量。</summary>
public sealed record WaveformReadProgress(
    int CompletedBlocks,
    int TotalBlocks,
    WaveformBlockDefinition CurrentBlock);
