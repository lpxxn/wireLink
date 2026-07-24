using System.Globalization;
using WireLink.Core.Communication;
using WireLink.Core.Models;

namespace WireLink.Core.Registers;

/// <summary>将原始寄存器按目录定义转换为可展示值，并完整记录计算过程。</summary>
public sealed class RegisterParser
{
    private static readonly string[] AlarmNames =
    [
        "负载监控1", "负载监控2", "过载预报警", "接地/漏电", "电流不平衡",
        "A相最大需用值", "B相最大需用值", "C相最大需用值", "N相最大需用值",
        "电压不平衡", "欠压", "过压", "逆功率", "欠频", "过频", "相序",
        "DI输入1", "DI输入2", "通讯失败", "触头磨损", "自诊断", "温度",
        "电压缺相", "长延时", "短延时", "瞬时",
    ];

    private static readonly string[] FaultTypes =
    [
        "无故障", "相序故障", "欠频故障", "过频故障", "欠压故障", "过压故障",
        "电压不平衡故障", "过载故障", "短路短延时反时限故障", "短路短延时定时限故障",
        "短路瞬时故障", "MCR动作", "HSISC动作", "接地故障", "漏电故障跳闸",
        "电流不平衡故障", "最大需用值溢出", "逆功率故障", "DI状态变化跳闸",
        "接地区域连锁跳闸", "短路区域连锁跳闸", "试验过载故障", "试验短路反时限故障",
        "试验短路定时限故障", "试验瞬时故障", "试验MCR动作", "试验HSISC动作",
        "试验接地故障", "试验漏电故障", "试验接地区域连锁", "试验短路区域连锁",
        "保留", "超温故障", "电压断相故障",
    ];

    private static readonly string[] AlarmTypes =
    [
        "无报警", "负载监控一电流报警", "负载监控二报警", "过载预报警", "接地报警",
        "漏电报警", "电流不平衡报警", "需用值溢出报警", "电压不平衡报警", "欠压报警",
        "过压报警", "逆功率报警", "欠频报警", "过频报警", "相序报警", "DI输入报警",
        "通讯链接失败报警", "自诊断报警", "触头磨损报警", "保留", "保留", "温度报警",
    ];

    private readonly IProtocolTrace _trace;

    public RegisterParser(IProtocolTrace? trace = null)
    {
        _trace = trace ?? NullProtocolTrace.Instance;
    }

    public IReadOnlyList<DecodedValue> Parse(
        IReadOnlyList<RegisterDefinition> definitions,
        IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        WordOrder wordOrder,
        FaultRecordType? faultRecordType = null,
        BreakerSeries controllerSeries = BreakerSeries.BW1)
    {
        var values = new List<DecodedValue>(definitions.Count);
        foreach (var definition in definitions)
        {
            var raw = definition.Addresses
                .Where(samples.ContainsKey)
                .Select(address => samples[address])
                .ToArray();

            if (raw.Length != definition.Addresses.Count)
            {
                continue;
            }

            values.Add(ParseOne(definition, raw, samples, wordOrder, faultRecordType, controllerSeries));
        }

        return values;
    }

    private DecodedValue ParseOne(
        RegisterDefinition definition,
        RawRegisterSample[] raw,
        IReadOnlyDictionary<ushort, RawRegisterSample> allSamples,
        WordOrder wordOrder,
        FaultRecordType? recordType,
        BreakerSeries controllerSeries)
    {
        var timestamp = raw.Max(sample => sample.ReadAt);
        try
        {
            var numeric = Combine(raw, definition.DataType, wordOrder);
            var (value, formula, status, warning) = definition.Transform switch
            {
                ValueTransform.Multiply => Scale(numeric, definition.Multiplier, definition.ProtocolConfirmed),
                ValueTransform.CurrentRatio => ScaleByCurrentRatio(numeric, allSamples, controllerSeries),
                ValueTransform.RatedCurrent => DecodeRatedCurrent(numeric, controllerSeries),
                ValueTransform.Percent => Percent(numeric),
                ValueTransform.RunStatus => (DecodeRunStatus((ushort)numeric), "按 5.2 位字段解析", ParseStatus.Success, null),
                ValueTransform.AlarmBits => (DecodeAlarmBits(numeric), "按 5.3 位字段解析", definition.ProtocolConfirmed ? ParseStatus.Success : ParseStatus.ProtocolUnconfirmed, definition.ProtocolConfirmed ? null : "uint32 字序未确认"),
                ValueTransform.CurrentEvent => DecodeEvent((ushort)numeric, allSamples, recordType),
                ValueTransform.EventData0 => DecodeEventData0((ushort)numeric, allSamples, recordType, controllerSeries),
                ValueTransform.EventAdditionalData => DecodeAdditionalEventData(numeric, allSamples, recordType),
                ValueTransform.EventData3Raw => DecodeEventData3Raw(numeric, allSamples, recordType),
                ValueTransform.RawUnconfirmed => ($"0x{numeric:X}", "未计算", ParseStatus.ProtocolUnconfirmed, "事件特定解析尚未完成或协议待确认"),
                ValueTransform.BcdDateTime => DecodeBcdDateTime(raw),
                ValueTransform.BcdYearMonth => DecodeBcdPair((ushort)numeric, "年", "月", 2000),
                ValueTransform.BcdDayHour => DecodeBcdPair((ushort)numeric, "日", "时", 0),
                ValueTransform.BcdMinuteSecond => DecodeBcdPair((ushort)numeric, "分", "秒", 0),
                ValueTransform.FaultRecordStatus => (DecodeRecordStatus((ushort)numeric), "按 5.6 位字段解析", ParseStatus.Success, null),
                ValueTransform.RecordSelector => (DecodeSelector((ushort)numeric), "L=类型，H=记录编号", ParseStatus.Success, null),
                _ => throw new ArgumentOutOfRangeException(),
            };

            if (definition.DataType == RegisterDataType.UInt32 && !definition.ProtocolConfirmed)
            {
                status = ParseStatus.ProtocolUnconfirmed;
                warning ??= "uint32 字序未确认";
                formula = $"{formula}；字序={wordOrder}";
            }

            var decoded = new DecodedValue(
                definition.Name,
                definition.Addresses,
                value,
                definition.Unit,
                formula,
                raw,
                status,
                warning,
                timestamp);

            _trace.Debug($"解析 {decoded.Name}：原始={decoded.RawText}；公式={decoded.Formula}；结果={decoded.DisplayValue}；状态={decoded.Status}");
            return decoded;
        }
        catch (Exception ex)
        {
            _trace.Warning($"解析 {definition.Name} 失败：{ex.Message}");
            return new DecodedValue(
                definition.Name,
                definition.Addresses,
                string.Join(" / ", raw.Select(sample => sample.HexValue)),
                definition.Unit,
                "解析失败",
                raw,
                ParseStatus.InvalidData,
                ex.Message,
                timestamp);
        }
    }

    private static uint Combine(RawRegisterSample[] raw, RegisterDataType type, WordOrder wordOrder)
    {
        if (type == RegisterDataType.UInt16)
        {
            return raw[0].Value;
        }

        var first = raw[0].Value;
        var second = raw[1].Value;
        return wordOrder == WordOrder.HighWordFirst
            ? ((uint)first << 16) | second
            : ((uint)second << 16) | first;
    }

    private static (string, string, ParseStatus, string?) Scale(uint raw, decimal multiplier, bool confirmed)
    {
        var scaled = raw * multiplier;
        var digits = DecimalDigits(multiplier);
        return (
            scaled.ToString(digits == 0 ? "0" : $"F{digits}", CultureInfo.InvariantCulture),
            $"{raw} × {multiplier.ToString(CultureInfo.InvariantCulture)}",
            confirmed ? ParseStatus.Success : ParseStatus.ProtocolUnconfirmed,
            confirmed ? null : "协议规则未实机确认");
    }

    private static (string, string, ParseStatus, string?) ScaleByCurrentRatio(
        uint raw,
        IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        BreakerSeries controllerSeries)
    {
        if (!samples.TryGetValue(787, out var ordinalSample))
        {
            return ($"0x{raw:X}", "缺少额定电流序值", ParseStatus.InvalidData, "未读取到寄存器 787");
        }

        if (ordinalSample.Value > byte.MaxValue)
        {
            return ($"0x{raw:X}", $"寄存器 787={ordinalSample.Value}", ParseStatus.InvalidData,
                "额定电流序值超出 byte 范围");
        }

        var ordinal = (byte)ordinalSample.Value;
        var ratio = CurrentRatioRule.Calculate(controllerSeries, ordinal);
        var ratedCurrent = CurrentRatioRule.GetRatedCurrent(controllerSeries, ordinal);
        var value = raw * (decimal)ratio;
        return (
            value.ToString("0", CultureInfo.InvariantCulture),
            $"{raw} × 电流变比(×{ratio}；{controllerSeries} 序值 {ordinal}={ratedCurrent}A)",
            ParseStatus.Success,
            null);
    }

    private static (string, string, ParseStatus, string?) DecodeRatedCurrent(
        uint raw,
        BreakerSeries controllerSeries)
    {
        if (raw > byte.MaxValue)
            return ($"0x{raw:X}", $"寄存器 787={raw}", ParseStatus.InvalidData, "额定电流序值超出 byte 范围");

        var ordinal = (byte)raw;
        var ratedCurrent = CurrentRatioRule.GetRatedCurrent(controllerSeries, ordinal);
        return (
            ratedCurrent.ToString(CultureInfo.InvariantCulture),
            $"{controllerSeries} 额定电流序值 {ordinal} → {ratedCurrent}A",
            ParseStatus.Success,
            null);
    }

    private static (string, string, ParseStatus, string?) Percent(uint raw)
    {
        return (raw.ToString(CultureInfo.InvariantCulture), "百分比原值直接显示", ParseStatus.Success, null);
    }

    /// <summary>
    /// 报警事件按已确认的 5.5.2 规则只有数据 0 有效：
    /// 当前报警的 517～523、历史报警的 773～779 均返回空显示值。
    /// 故障和变位事件按当前约定直接显示十进制原始值。
    /// </summary>
    private static (string, string, ParseStatus, string?) DecodeAdditionalEventData(
        uint raw,
        IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        FaultRecordType? recordType)
    {
        var effectiveType = ResolveEventType(samples, recordType);
        return effectiveType switch
        {
            FaultRecordType.Alarm =>
                (string.Empty, "报警仅数据 0 有效，本字段为空", ParseStatus.Success, null),
            FaultRecordType.Fault or FaultRecordType.StateChange =>
                (raw.ToString(CultureInfo.InvariantCulture), "事件数据原始值直接显示", ParseStatus.Success, null),
            _ when samples.ContainsKey(512) =>
                (string.Empty, "当前无故障/报警，本字段为空", ParseStatus.Success, null),
            _ =>
                ($"0x{raw:X}", "缺少事件类别", ParseStatus.InvalidData, "无法确定事件类别"),
        };
    }

    /// <summary>
    /// 故障数据 3 暂不关联保护设置，直接显示寄存器十进制原始值；
    /// 报警事件仍遵循只有数据 0 有效的规则。
    /// </summary>
    private static (string, string, ParseStatus, string?) DecodeEventData3Raw(
        uint raw,
        IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        FaultRecordType? recordType)
    {
        var effectiveType = ResolveEventType(samples, recordType);
        return effectiveType switch
        {
            FaultRecordType.Alarm =>
                (string.Empty, "报警仅数据 0 有效，本字段为空", ParseStatus.Success, null),
            FaultRecordType.Fault =>
                (raw.ToString(CultureInfo.InvariantCulture), "故障数据 3 原始值直接显示", ParseStatus.Success, null),
            FaultRecordType.StateChange =>
                (raw.ToString(CultureInfo.InvariantCulture), "变位事件数据原始值直接显示", ParseStatus.Success, null),
            _ when samples.ContainsKey(512) =>
                (string.Empty, "当前无故障/报警，本字段为空", ParseStatus.Success, null),
            _ =>
                ($"0x{raw:X}", "缺少事件类别", ParseStatus.InvalidData, "无法确定事件类别"),
        };
    }

    /// <summary>
    /// 解析 5.5 表中的事件数据 0。电流事件使用控制器系列和寄存器 787 的额定电流序值计算变比；
    /// 百分比按用户确认后的规则直接显示原值，不再除以 100。
    /// </summary>
    private static (string, string, ParseStatus, string?) DecodeEventData0(
        ushort raw,
        IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        FaultRecordType? recordType,
        BreakerSeries controllerSeries)
    {
        var eventAddress = samples.ContainsKey(515) ? (ushort)515 : (ushort)771;
        if (!samples.TryGetValue(eventAddress, out var eventSample))
            return ($"0x{raw:X4}", "缺少事件类型", ParseStatus.InvalidData, $"未读取到寄存器 {eventAddress}");

        var typeCode = (byte)(eventSample.Value >> 8);
        var effectiveType = ResolveEventType(samples, recordType);

        return effectiveType switch
        {
            FaultRecordType.Fault => DecodeFaultData0(raw, typeCode, samples, controllerSeries),
            FaultRecordType.Alarm => DecodeAlarmData0(raw, typeCode, samples, controllerSeries),
            FaultRecordType.StateChange => ($"0x{raw:X4}", "按 5.5 读取变位记录数据 0 原值", ParseStatus.Success, null),
            _ when samples.ContainsKey(512) =>
                (string.Empty, "当前无故障/报警，本字段为空", ParseStatus.Success, null),
            _ => ($"0x{raw:X4}", "无法确定事件类别", ParseStatus.ProtocolUnconfirmed, "当前无有效故障/报警类别"),
        };
    }

    private static (string, string, ParseStatus, string?) DecodeFaultData0(
        ushort raw, byte typeCode, IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        BreakerSeries controllerSeries)
    {
        // 过载、短路、接地、联锁、最大需用及相应试验事件使用电流变比。
        int[] ratioTypes = [7, 8, 9, 10, 13, 16, 19, 20, 21, 22, 23, 24, 27, 29, 30];
        if (ratioTypes.Contains(typeCode)) return EventCurrent(raw, samples, controllerSeries);
        if (typeCode is 14 or 28) return ($"{raw * 0.01m:0.00} A", $"{raw} × 0.01（漏电）", ParseStatus.Success, null);
        if (typeCode is 15 or 6) return ($"{raw}%", "百分比原值直接显示", ParseStatus.Success, null);
        if (typeCode is 4 or 5) return ($"{raw} V", "原值 × 1", ParseStatus.Success, null);
        if (typeCode is 2 or 3) return ($"{raw * 0.01m:0.00} Hz", $"{raw} × 0.01", ParseStatus.Success, null);
        if (typeCode == 1) return (raw switch { 1 => "ABC", 2 => "ACB", _ => $"未知相序 {raw}" }, "相序代码", raw is 1 or 2 ? ParseStatus.Success : ParseStatus.InvalidData, raw is 1 or 2 ? null : "未知相序代码");
        if (typeCode == 17) return ($"{unchecked((short)raw)} kW", "按 int16 有符号值 × 1", ParseStatus.Success, null);
        return ($"0x{raw:X4}", "该事件数据 0 不计算", ParseStatus.ProtocolUnconfirmed, "该事件的数据 0 含义未实现或无意义");
    }

    private static (string, string, ParseStatus, string?) DecodeAlarmData0(
        ushort raw, byte typeCode, IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        BreakerSeries controllerSeries)
    {
        if (typeCode is 1 or 2 or 3 or 4 or 7) return EventCurrent(raw, samples, controllerSeries);
        if (typeCode == 5) return ($"{raw * 0.01m:0.00} A", $"{raw} × 0.01（漏电）", ParseStatus.Success, null);
        if (typeCode is 6 or 8) return ($"{raw}%", "百分比原值直接显示", ParseStatus.Success, null);
        if (typeCode is 9 or 10) return ($"{raw} V", "原值 × 1", ParseStatus.Success, null);
        if (typeCode == 11) return ($"{unchecked((short)raw)} kW", "按 int16 有符号值 × 1", ParseStatus.Success, null);
        if (typeCode is 12 or 13) return ($"{raw * 0.01m:0.00} Hz", $"{raw} × 0.01", ParseStatus.Success, null);
        if (typeCode == 14) return (raw switch { 1 => "ABC", 2 => "ACB", _ => $"未知相序 {raw}" }, "相序代码", raw is 1 or 2 ? ParseStatus.Success : ParseStatus.InvalidData, raw is 1 or 2 ? null : "未知相序代码");
        if (typeCode == 21) return ($"{unchecked((short)raw) * 0.1m:0.0} ℃", $"int16({unchecked((short)raw)}) × 0.1", ParseStatus.Success, null);
        return ($"0x{raw:X4}", "该报警数据 0 不计算", ParseStatus.ProtocolUnconfirmed, "该报警的数据 0 含义未实现或无意义");
    }

    private static (string, string, ParseStatus, string?) EventCurrent(
        ushort raw, IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        BreakerSeries controllerSeries)
    {
        var result = ScaleByCurrentRatio(raw, samples, controllerSeries);
        return result.Item3 == ParseStatus.Success
            ? ($"{result.Item1} A", result.Item2, result.Item3, result.Item4)
            : result;
    }

    private static (string, string, ParseStatus, string?) DecodeBcdPair(ushort value, string highLabel, string lowLabel, int highOffset)
    {
        var high = DecodeBcd((byte)(value >> 8)) + highOffset;
        var low = DecodeBcd((byte)value);
        return ($"{highLabel} {high:00} / {lowLabel} {low:00}", "高/低字节分别按 BCD 解码", ParseStatus.Success, null);
    }

    private static (string, string, ParseStatus, string?) DecodeBcdDateTime(
        IReadOnlyList<RawRegisterSample> samples)
    {
        if (samples.Count != 3)
            throw new FormatException("完整时间必须包含年月、日时、分秒三个寄存器。");

        var year = 2000 + DecodeBcd((byte)(samples[0].Value >> 8));
        var month = DecodeBcd((byte)samples[0].Value);
        var day = DecodeBcd((byte)(samples[1].Value >> 8));
        var hour = DecodeBcd((byte)samples[1].Value);
        var minute = DecodeBcd((byte)(samples[2].Value >> 8));
        var second = DecodeBcd((byte)samples[2].Value);

        if (month is < 1 or > 12) throw new FormatException($"无效月份 {month}");
        if (day is < 1 or > 31) throw new FormatException($"无效日期 {day}");
        if (hour > 23) throw new FormatException($"无效小时 {hour}");
        if (minute > 59) throw new FormatException($"无效分钟 {minute}");
        if (second > 59) throw new FormatException($"无效秒 {second}");

        return (
            $"{year:0000}-{month:00}-{day:00} {hour:00}:{minute:00}:{second:00}",
            "768～770/780～782 按 BCD 组合时间",
            ParseStatus.Success,
            null);
    }

    private static int DecodeBcd(byte value)
    {
        var high = value >> 4;
        var low = value & 0x0F;
        if (high > 9 || low > 9)
        {
            throw new FormatException($"无效 BCD 字节 0x{value:X2}");
        }

        return high * 10 + low;
    }

    private static string DecodeRunStatus(ushort value)
    {
        var breaker = (value & 0x03) switch
        {
            0 => "分闸",
            1 => "分闸中",
            2 => "合闸",
            _ => "合闸中",
        };
        var parts = new List<string> { breaker };
        AddFlag(parts, value, 2, "有报警");
        AddFlag(parts, value, 3, "故障跳闸");
        AddFlag(parts, value, 10, "新故障");
        AddFlag(parts, value, 11, "新报警");
        AddFlag(parts, value, 12, "新变位");
        var diagnostic = (value >> 13) & 0x07;
        if (diagnostic != 0)
        {
            parts.Add($"自诊断代码 {diagnostic}");
        }

        return string.Join("；", parts);
    }

    private static string DecodeAlarmBits(uint value)
    {
        var active = AlarmNames
            .Select((name, bit) => (name, bit))
            .Where(item => (value & (1u << item.bit)) != 0)
            .Select(item => item.name)
            .ToArray();
        return active.Length == 0 ? "无当前报警" : string.Join("、", active);
    }

    private static (string, string, ParseStatus, string?) DecodeEvent(
        ushort value,
        IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        FaultRecordType? recordType)
    {
        var phaseCode = value & 0xFF;
        var typeCode = value >> 8;
        var phase = phaseCode switch { 0 => "A相", 1 => "B相", 2 => "C相", 3 => "N相", _ => "无特定相别" };

        var effectiveType = ResolveEventType(samples, recordType);
        if (effectiveType is null && samples.ContainsKey(512))
            return ("无当前故障/报警", "按 512 故障/报警标志判断", ParseStatus.Success, null);

        var typeName = effectiveType switch
        {
            FaultRecordType.Fault when typeCode < FaultTypes.Length => FaultTypes[typeCode],
            FaultRecordType.Alarm when typeCode < AlarmTypes.Length => AlarmTypes[typeCode],
            FaultRecordType.StateChange => $"变位类型 {typeCode}",
            _ => $"类型码 {typeCode}",
        };

        var status = effectiveType is null ? ParseStatus.ProtocolUnconfirmed : ParseStatus.Success;
        var warning = status == ParseStatus.Success ? null : "当前没有有效的故障或报警标志";
        return ($"{phase} / {typeName}", "L=相别，H=类型", status, warning);
    }

    private static FaultRecordType? ResolveEventType(
        IReadOnlyDictionary<ushort, RawRegisterSample> samples,
        FaultRecordType? recordType)
    {
        if (recordType is not null)
            return recordType;

        if (!samples.TryGetValue(512, out var runStatus))
            return null;

        var hasAlarm = (runStatus.Value & (1 << 2)) != 0;
        var hasFault = (runStatus.Value & (1 << 3)) != 0;
        // bit3 表示故障跳闸，优先级高于 bit2 报警；两者同时为 1 时按故障解析。
        return hasFault ? FaultRecordType.Fault : hasAlarm ? FaultRecordType.Alarm : null;
    }

    private static string DecodeRecordStatus(ushort value)
    {
        var faults = (value >> 1) & 0x0F;
        var alarms = (value >> 5) & 0x0F;
        var changes = (value >> 9) & 0x0F;
        return $"故障 {faults} / 报警 {alarms} / 变位 {changes}";
    }

    private static string DecodeSelector(ushort value)
    {
        var type = (FaultRecordType)(value & 0xFF);
        var index = value >> 8;
        var typeName = type switch
        {
            FaultRecordType.Fault => "故障",
            FaultRecordType.Alarm => "报警",
            FaultRecordType.StateChange => "变位",
            _ => $"未知类型 {(byte)type}",
        };
        return $"{typeName} / 记录 {index}";
    }

    private static int DecimalDigits(decimal multiplier)
    {
        var bits = decimal.GetBits(multiplier);
        return (bits[3] >> 16) & 0x7F;
    }

    private static void AddFlag(List<string> target, ushort value, int bit, string text)
    {
        if ((value & (1 << bit)) != 0)
        {
            target.Add(text);
        }
    }
}
