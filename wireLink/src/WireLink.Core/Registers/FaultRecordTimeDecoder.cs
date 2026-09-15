using System.Globalization;

namespace WireLink.Core.Registers;

/// <summary>将故障记录区 768～770 的三个 BCD 寄存器解析为本地墙上时间。</summary>
public static class FaultRecordTimeDecoder
{
    public static DateTime Decode(ushort yearMonth, ushort dayHour, ushort minuteSecond)
    {
        if (yearMonth == 0 && dayHour == 0 && minuteSecond == 0)
            throw new FormatException("故障记录时间为空（768～770 均为 0000H）。");

        var year = 2000 + DecodeBcd((byte)(yearMonth >> 8));
        var month = DecodeBcd((byte)yearMonth);
        var day = DecodeBcd((byte)(dayHour >> 8));
        var hour = DecodeBcd((byte)dayHour);
        var minute = DecodeBcd((byte)(minuteSecond >> 8));
        var second = DecodeBcd((byte)minuteSecond);

        try
        {
            // 设备报文没有时区信息，因此保留为 Unspecified，避免操作系统进行意外的时区换算。
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            var value = $"{year:0000}-{month:00}-{day:00} {hour:00}:{minute:00}:{second:00}";
            throw new FormatException($"故障记录时间不是有效日期：{value}。", ex);
        }
    }

    public static string Format(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static int DecodeBcd(byte value)
    {
        var high = value >> 4;
        var low = value & 0x0F;
        if (high > 9 || low > 9)
            throw new FormatException($"故障记录时间包含无效 BCD 字节 0x{value:X2}。");

        return high * 10 + low;
    }
}
