namespace WireLink.Core.Models;

public enum WordOrder
{
    HighWordFirst,
    LowWordFirst,
}

public enum ParseStatus
{
    Success,
    Stale,
    ProtocolUnconfirmed,
    InvalidData,
    ReadFailed,
}

public enum RegisterDataType
{
    UInt16,
    UInt32,
}

public enum ValueTransform
{
    Multiply,
    CurrentRatio,
    Percent,
    RunStatus,
    AlarmBits,
    CurrentEvent,
    RawUnconfirmed,
    BcdYearMonth,
    BcdDayHour,
    BcdMinuteSecond,
    FaultRecordStatus,
    RecordSelector,
}

public enum FaultRecordType : byte
{
    Fault = 0,
    Alarm = 1,
    StateChange = 2,
}

/// <summary>一个逻辑字段的协议元数据。</summary>
public sealed record RegisterDefinition(
    string Name,
    IReadOnlyList<ushort> Addresses,
    RegisterDataType DataType,
    string Unit,
    ValueTransform Transform,
    decimal Multiplier = 1m,
    string FormatDescription = "×1",
    bool ProtocolConfirmed = true);

/// <summary>单个 16 位寄存器的原始采样。</summary>
public sealed record RawRegisterSample(ushort Address, ushort Value, DateTimeOffset ReadAt)
{
    public string HexValue => $"0x{Value:X4}";
}

/// <summary>可直接用于界面和 Excel 的解析结果。</summary>
public sealed record DecodedValue(
    string Name,
    IReadOnlyList<ushort> Addresses,
    string Value,
    string Unit,
    string Formula,
    IReadOnlyList<RawRegisterSample> RawSamples,
    ParseStatus Status,
    string? Warning,
    DateTimeOffset ReadAt)
{
    public string DisplayValue => string.IsNullOrWhiteSpace(Unit) ? Value : $"{Value} {Unit}";

    public string AddressText => string.Join(", ", Addresses);

    public string RawText => string.Join(" / ", RawSamples.Select(sample => $"{sample.Address}:{sample.HexValue}"));
}

/// <summary>一次跨多个非连续区间读取的结果。</summary>
public sealed record DataReadResult(
    IReadOnlyList<DecodedValue> Values,
    IReadOnlyList<string> Errors,
    DateTimeOffset ReadAt)
{
    public bool IsComplete => Errors.Count == 0;
    public bool HasData => Values.Count > 0;
}

public sealed record RegisterBlock(ushort StartAddress, ushort Count)
{
    public ushort EndAddress => checked((ushort)(StartAddress + Count - 1));
}
