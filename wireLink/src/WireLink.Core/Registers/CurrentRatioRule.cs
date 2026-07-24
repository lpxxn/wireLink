namespace WireLink.Core.Registers;

/// <summary>协议备注中定义额定电流序值的控制器系列。</summary>
public enum BreakerSeries
{
    BW1,
    BW3,
}

/// <summary>
/// 根据控制器系列和寄存器 787 返回的额定电流序值，
/// 查询额定电流并计算电流变比。
/// </summary>
public static class CurrentRatioRule
{
    private static readonly ushort[] Bw1RatedCurrents =
    [
        200, 250, 315, 400, 630, 800, 1000, 1250, 1600, 1900,
        2000, 2002, 2500, 2900, 3150, 3200, 3600, 3900, 4000, 4003,
        4900, 5000, 5900, 6300,
    ];

    private static readonly ushort[] Bw3RatedCurrents =
    [
        200, 250, 315, 400, 630, 800, 1000, 1250, 1600, 1900,
        2000, 2500, 1600, 2000, 2500, 2900, 3150, 3200, 3600, 3900,
        4000, 4000, 4900, 5000, 5900, 6300,
    ];

    /// <summary>根据控制器系列和额定电流序值查询实际额定电流，单位 A。</summary>
    public static ushort GetRatedCurrent(BreakerSeries series, byte ratedCurrentOrdinal)
    {
        var table = series == BreakerSeries.BW1 ? Bw1RatedCurrents : Bw3RatedCurrents;
        if (ratedCurrentOrdinal >= table.Length)
            throw new ArgumentOutOfRangeException(nameof(ratedCurrentOrdinal),
                $"{series} 的额定电流序值必须在 0～{table.Length - 1} 之间。");

        return table[ratedCurrentOrdinal];
    }

    /// <summary>BW1 序值 0～10 为 1、其余为 2；BW3 序值 0～11 为 1、其余为 2。</summary>
    public static ushort Calculate(BreakerSeries series, byte ratedCurrentOrdinal)
    {
        _ = GetRatedCurrent(series, ratedCurrentOrdinal);

        var upperBoundForRatioOne = series == BreakerSeries.BW1 ? 10 : 11;
        return ratedCurrentOrdinal <= upperBoundForRatioOne ? (ushort)1 : (ushort)2;
    }
}
