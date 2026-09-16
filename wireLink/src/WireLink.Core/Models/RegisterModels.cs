namespace WireLink.Core.Models;

public enum WordOrder
{
    HighWordFirst,
    LowWordFirst,
}

public enum DeviceType
{
    FrameController,
    MoldedCaseCircuitBreaker,
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

/// <summary>
/// 寄存器原始值到界面展示值的变换规则。
/// <see cref="Registers.RegisterParser"/> 按本枚举分发到对应纯函数；新增规则时需同时加解析分支和测试。
/// </summary>
/// <remarks>
/// 倍率类规则读 <see cref="RegisterDefinition.Multiplier"/>；码表、位字段、BCD 和时间类规则忽略倍率。
/// 协议尚未实机确认的字段应配合 <see cref="RegisterDefinition.ProtocolConfirmed"/>，解析结果用
/// <see cref="ParseStatus.ProtocolUnconfirmed"/> 或 <see cref="ParseStatus.InvalidData"/>，不得在 UI 静默猜测。
/// </remarks>
public enum ValueTransform
{
    /// <summary>原值 × <see cref="RegisterDefinition.Multiplier"/>。用于电压、功率、简单电流等线性量。</summary>
    Multiply,

    /// <summary>
    /// 原值 × 电流变比。变比由控制器系列和寄存器 1552 的 bit0～bit7（额定电流序值）决定；
    /// 缺少 1552 时标记 <see cref="ParseStatus.InvalidData"/>。
    /// </summary>
    CurrentRatio,

    /// <summary>
    /// 寄存器 1552：只取 bit0～bit7 作为额定电流序值，再按控制器系列映射为额定电流（A）。
    /// 高 8 位含框架等级等，不得把整个 uint16 当序值。
    /// </summary>
    RatedCurrent,

    /// <summary>百分比原值直接显示，不再除以 100。用于热容等协议已按百分数编码的字段。</summary>
    Percent,

    /// <summary>
    /// 运行状态（协议 5.2）。bit0～1 分合闸；bit2 报警、bit3 故障跳闸；
    /// bit10～12 新故障/报警/变位；bit13～15 自诊断代码。
    /// </summary>
    RunStatus,

    /// <summary>
    /// 当前报警位图（协议 5.3）。按位置出已置位的报警名称；全 0 显示「无当前报警」。
    /// uint32 字序未确认时标记 <see cref="ParseStatus.ProtocolUnconfirmed"/>。
    /// </summary>
    AlarmBits,

    /// <summary>
    /// 故障/报警/变位相别和类型（协议 5.4）。低字节相别（A/B/C/N），高字节类型码。
    /// 当前事件依寄存器 512 的故障/报警标志选码表；历史记录用调用方传入的 <see cref="FaultRecordType"/>。
    /// </summary>
    CurrentEvent,

    /// <summary>
    /// 事件数据 0（协议 5.5）。按事件类别和类型码换算电流（变比）、漏电、百分比、电压、频率、相序、功率等；
    /// 报警仅数据 0 有效。依赖 515 或 771 的事件字，以及电流类事件所需的 1552。
    /// </summary>
    EventData0,

    /// <summary>
    /// 事件附加数据（当前报警 517～523、历史 773～779 等，不含数据 0 和数据 3）。
    /// 报警事件显示空；故障和变位显示十进制原值；当前无故障/报警时显示空。
    /// </summary>
    EventAdditionalData,

    /// <summary>
    /// 故障数据 3：暂不关联保护定值，十进制原值直接显示。报警事件仍为空。
    /// </summary>
    EventData3Raw,

    /// <summary>
    /// 协议待确认或事件特定解析未实现：以十六进制原值展示，状态为 <see cref="ParseStatus.ProtocolUnconfirmed"/>。
    /// </summary>
    RawUnconfirmed,

    /// <summary>
    /// 三个连续寄存器按 BCD 组成完整时间（年/月、日/时、分/秒），用于 768～770、780～782。
    /// </summary>
    BcdDateTime,

    /// <summary>单寄存器高/低字节分别按 BCD 解码为「年 / 月」；年份加 2000。</summary>
    BcdYearMonth,

    /// <summary>单寄存器高/低字节分别按 BCD 解码为「日 / 时」。</summary>
    BcdDayHour,

    /// <summary>单寄存器高/低字节分别按 BCD 解码为「分 / 秒」。</summary>
    BcdMinuteSecond,

    /// <summary>
    /// 故障记录状态标志（协议 5.6）。bit1～4 故障条数、bit5～8 报警条数、bit9～12 变位条数。
    /// </summary>
    FaultRecordStatus,

    /// <summary>
    /// 指定读取的记录（寄存器 785）。低字节为 <see cref="FaultRecordType"/>，高字节为第几条记录。
    /// </summary>
    RecordSelector,

    /// <summary>塑壳相别码表（协议 6.1）：0～3 为 A/B/C/N 相；未定义原值保留十进制并标记未确认。</summary>
    MoldedCasePhase,

    /// <summary>
    /// 塑壳故障类型码表（协议 6.2）：0 无故障，1 瞬时、2 漏电、4 接地、8 短延时、16 长延时；
    /// 32/33/34/36/40/48 表示故障未读取。
    /// </summary>
    MoldedCaseFaultType,

    /// <summary>塑壳长延时时间 T1（协议 6.5）：0=OFF，1～150 为秒数；超出范围未确认。</summary>
    MoldedCaseLongDelayTime,

    /// <summary>塑壳短延时时间 T2（协议 6.6）：0=OFF，3/5/10/15 分别映射 0.06/0.1/0.2/0.3 s。</summary>
    MoldedCaseShortDelayTime,

    /// <summary>塑壳接地时间 Tg（协议 6.7）：0～7 映射 (原值+1)/10 秒，8=报警。</summary>
    MoldedCaseGroundTime,

    /// <summary>塑壳预报警时间 Tp（协议 6.8）：0～9 映射 (原值+1)/10 秒。</summary>
    MoldedCasePreAlarmTime,

    /// <summary>
    /// 框架接地保护方式：寄存器 1793 的 bit12～bit10。0 关闭、1 漏电型、2 差值型（换算未实现）、3 地电流型。
    /// 后续接地/漏电电流与时间字段依赖本结果选择换算。
    /// </summary>
    FrameGroundProtectionMode,

    /// <summary>框架 N 相保护设置（协议 5.14）：0～3 为 50%/100%/160%/200%，4=关闭。</summary>
    FrameNPhaseProtection,

    /// <summary>
    /// 框架接地或漏电电流（动作值、报警启动/返回值）。依 1793.bit12～bit10：
    /// 漏电型 ×0.01 A；地电流型 × 电流变比；关闭/差值型/保留值不换算。
    /// </summary>
    FrameGroundOrLeakageCurrent,

    /// <summary>
    /// 框架接地或漏电动作时间。地电流型 ×0.01 s；漏电型按协议 5.15 枚举（瞬时及 0.06～0.83 s）。
    /// </summary>
    FrameGroundOrLeakageActionTime,

    /// <summary>
    /// 取 uint16 低 8 位后再 × <see cref="RegisterDefinition.Multiplier"/>。
    /// 用于同一寄存器打包两个量，例如 1298 报警启动时间、1299 I 不平衡启动值。
    /// </summary>
    LowByte,

    /// <summary>
    /// 取 uint16 高 8 位后再 × <see cref="RegisterDefinition.Multiplier"/>。
    /// 与 <see cref="LowByte"/> 配对，例如 1298 报警返回时间、1299 I 不平衡返回值。
    /// </summary>
    HighByte,
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
    public string DisplayValue => string.IsNullOrWhiteSpace(Unit)
        ? Value
        : Unit == "%" ? $"{Value}%" : $"{Value} {Unit}";

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
