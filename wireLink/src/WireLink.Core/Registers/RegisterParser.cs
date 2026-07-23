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
        FaultRecordType? faultRecordType = null)
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

            values.Add(ParseOne(definition, raw, samples, wordOrder, faultRecordType));
        }

        return values;
    }

    private DecodedValue ParseOne(
        RegisterDefinition definition,
        RawRegisterSample[] raw,
        IReadOnlyDictionary<ushort, RawRegisterSample> allSamples,
        WordOrder wordOrder,
        FaultRecordType? recordType)
    {
        var timestamp = raw.Max(sample => sample.ReadAt);
        try
        {
            var numeric = Combine(raw, definition.DataType, wordOrder);
            var (value, formula, status, warning) = definition.Transform switch
            {
                ValueTransform.Multiply => Scale(numeric, definition.Multiplier, definition.ProtocolConfirmed),
                ValueTransform.CurrentRatio => ScaleByCurrentRatio(numeric, allSamples),
                ValueTransform.Percent => Percent(numeric),
                ValueTransform.RunStatus => (DecodeRunStatus((ushort)numeric), "按 5.2 位字段解析", ParseStatus.Success, null),
                ValueTransform.AlarmBits => (DecodeAlarmBits(numeric), "按 5.3 位字段解析", ParseStatus.ProtocolUnconfirmed, "uint32 字序未实机确认"),
                ValueTransform.CurrentEvent => DecodeEvent((ushort)numeric, allSamples, recordType),
                ValueTransform.RawUnconfirmed => ($"0x{numeric:X}", "未计算", ParseStatus.ProtocolUnconfirmed, "倍率或事件含义待协议确认"),
                ValueTransform.BcdYearMonth => DecodeBcdPair((ushort)numeric, "年", "月", 2000),
                ValueTransform.BcdDayHour => DecodeBcdPair((ushort)numeric, "日", "时", 0),
                ValueTransform.BcdMinuteSecond => DecodeBcdPair((ushort)numeric, "分", "秒", 0),
                ValueTransform.FaultRecordStatus => (DecodeRecordStatus((ushort)numeric), "按 5.6 位字段解析", ParseStatus.Success, null),
                ValueTransform.RecordSelector => (DecodeSelector((ushort)numeric), "L=类型，H=序号", ParseStatus.Success, "记录新旧顺序待确认"),
                _ => throw new ArgumentOutOfRangeException(),
            };

            if (definition.DataType == RegisterDataType.UInt32 && !definition.ProtocolConfirmed)
            {
                status = ParseStatus.ProtocolUnconfirmed;
                warning ??= "uint32 字序未实机确认";
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
        IReadOnlyDictionary<ushort, RawRegisterSample> samples)
    {
        if (!samples.TryGetValue(788, out var ratio))
        {
            return ($"0x{raw:X}", "缺少电流变比", ParseStatus.InvalidData, "未读取到寄存器 788");
        }

        var value = raw * (decimal)ratio.Value;
        return (value.ToString("0", CultureInfo.InvariantCulture), $"{raw} × 电流变比({ratio.Value})", ParseStatus.Success, null);
    }

    private static (string, string, ParseStatus, string?) Percent(uint raw)
    {
        var value = raw / 100m;
        return (value.ToString("F2", CultureInfo.InvariantCulture), $"{raw} ÷ 100", ParseStatus.Success, null);
    }

    private static (string, string, ParseStatus, string?) DecodeBcdPair(ushort value, string highLabel, string lowLabel, int highOffset)
    {
        var high = DecodeBcd((byte)(value >> 8)) + highOffset;
        var low = DecodeBcd((byte)value);
        return ($"{highLabel} {high:00} / {lowLabel} {low:00}", "高/低字节分别按 BCD 解码", ParseStatus.Success, null);
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

        var effectiveType = recordType;
        if (effectiveType is null && samples.TryGetValue(512, out var runStatus))
        {
            var hasAlarm = (runStatus.Value & (1 << 2)) != 0;
            var hasFault = (runStatus.Value & (1 << 3)) != 0;
            if (hasAlarm && hasFault)
            {
                return ($"相别码 {phaseCode} / 类型码 {typeCode}", "未解析", ParseStatus.ProtocolUnconfirmed, "故障和报警同时有效，协议未说明优先级");
            }

            effectiveType = hasFault ? FaultRecordType.Fault : hasAlarm ? FaultRecordType.Alarm : null;
        }

        var typeName = effectiveType switch
        {
            FaultRecordType.Fault when typeCode < FaultTypes.Length => FaultTypes[typeCode],
            FaultRecordType.Alarm when typeCode < AlarmTypes.Length => AlarmTypes[typeCode],
            FaultRecordType.StateChange => $"变位类型 {typeCode}",
            _ => $"类型码 {typeCode}",
        };

        var status = effectiveType == FaultRecordType.StateChange || effectiveType is null
            ? ParseStatus.ProtocolUnconfirmed
            : ParseStatus.Success;
        var warning = status == ParseStatus.Success ? null : "事件类型规则待补充";
        return ($"{phase} / {typeName}", "L=相别，H=类型", status, warning);
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
