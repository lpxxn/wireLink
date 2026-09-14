using WireLink.Core.Models;

namespace WireLink.Core.Registers;

/// <summary>
/// 一次界面页面对应的 Modbus 读取计划：先按 <see cref="Blocks"/> 分段读保持寄存器，
/// 再按 <see cref="Definitions"/> 把原始字解析成展示字段。
/// </summary>
/// <param name="Blocks">
/// 连续读取区间。同一逻辑值的多寄存器必须落在同一块内；
/// 无依赖的字段可拆成独立块，避免单地址失败连带清空整页。
/// </param>
/// <param name="Definitions">该页要解析并展示的字段目录，地址须被 <paramref name="Blocks"/> 覆盖。</param>
public sealed record RegisterPageProfile(
    IReadOnlyList<RegisterBlock> Blocks,
    IReadOnlyList<RegisterDefinition> Definitions);

/// <summary>
/// 一种从站设备的协议画像：连接探测用哪个寄存器，设备数据/保护数据各读哪些区间和字段。
/// 扫描从站、测连通、读设备数据和读保护数据都通过本记录取地址，而不是在 UI 里硬编码。
/// </summary>
/// <param name="DeviceType">设备类型枚举，与界面选择和 <see cref="DeviceProfileCatalog.Get"/> 对应。</param>
/// <param name="DisplayName">界面展示用中文名称，例如「框架控制器」「塑壳断路器」。</param>
/// <param name="ProbeRegister">
/// 探测用保持寄存器地址（功能码 03）。扫描从站和测连接时只读 1 个字；
/// 读成功即认为该地址上存在该类型设备。框架控制器为 0x0100（额定电流），塑壳为 0x0001（A 相电流）。
/// </param>
/// <param name="DeviceData">设备数据页的读取块与字段定义（电流、状态等运行量）。</param>
/// <param name="ProtectionData">
/// 保护定值页；仅塑壳断路器有。为 <see langword="null"/> 时表示该类型没有保护数据页，调用方不得读取。
/// </param>
public sealed record DeviceProfile(
    DeviceType DeviceType,
    string DisplayName,
    ushort ProbeRegister,
    RegisterPageProfile DeviceData,
    RegisterPageProfile? ProtectionData = null);

/// <summary>
/// 各 <see cref="DeviceType"/> 的静态协议目录：探测地址、设备数据区间和（可选）保护定值区间。
/// 框架控制器复用 <see cref="RegisterCatalog"/>；塑壳断路器在本类内按协议第 6 章单独列出。
/// </summary>
public static class DeviceProfileCatalog
{
    /// <summary>
    /// 框架控制器（万能式断路器控制器）画像。
    /// 探测 0x0100；设备数据沿用 <see cref="RegisterCatalog.DeviceBlocks"/> /
    /// <see cref="RegisterCatalog.DeviceDefinitions"/>；无保护数据页。
    /// </summary>
    public static DeviceProfile FrameController { get; } = new(
        DeviceType.FrameController,
        "框架控制器",
        0x0100,
        new RegisterPageProfile(RegisterCatalog.DeviceBlocks, RegisterCatalog.DeviceDefinitions));

    /// <summary>
    /// 塑壳断路器画像。探测 0x0001（A 相电流）。
    /// 设备数据为相电流、故障记录等；保护数据为长/短延时、瞬动、接地、预报警等定值。
    /// 带 <c>FormatDescription: "见 6.x"</c> 的字段按协议第 6 章码表变换，不是简单倍率。
    /// </summary>
    public static DeviceProfile MoldedCaseCircuitBreaker { get; } = new(
        DeviceType.MoldedCaseCircuitBreaker,
        "塑壳断路器",
        0x0001,
        new RegisterPageProfile(
            [new(0x0001, 7), new(0x0032, 4)],
            [
                Number("A 相电流", 0x0001, "A"),
                Number("B 相电流", 0x0002, "A"),
                Number("C 相电流", 0x0003, "A"),
                Number("N 相电流", 0x0004, "A"),
                Number("接地电流", 0x0005, "A"),
                Number("最大相电流", 0x0006, "A"),
                new("最大电流所在相", [0x0007], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.MoldedCasePhase, FormatDescription: "见 6.1"),
                new("故障类型记录", [0x0032], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.MoldedCaseFaultType, FormatDescription: "见 6.2"),
                Number("故障最大相电流记录", 0x0033, string.Empty),
                new("故障电流所在相记录", [0x0034], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.MoldedCasePhase, FormatDescription: "见 6.1"),
                Number("故障时间记录", 0x0035, "s", 0.02m),
            ]),
        new RegisterPageProfile(
            [new(0x0016, 7), new(0x001E, 2)],
            [
                Number("长延时电流设定值 Ir1", 0x0016, "A"),
                new("长延时时间设定值 T1", [0x0017], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.MoldedCaseLongDelayTime, FormatDescription: "见 6.5"),
                Number("短延时电流设定值 Ir2", 0x0018, "A"),
                new("短延时时间设定值 T2", [0x0019], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.MoldedCaseShortDelayTime, FormatDescription: "见 6.6"),
                Number("瞬动电流设定值 Ir3", 0x001A, "A"),
                Number("接地电流设定值 Ir4", 0x001B, "A"),
                new("接地时间设定值 Tg", [0x001C], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.MoldedCaseGroundTime, FormatDescription: "见 6.7"),
                new("漏电电流", [0x001D], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.Multiply, FormatDescription: "协议保留地址，不读取",
                    IsReadable: false, FixedValue: "0"),
                new("预报警时间设定值 Tp", [0x001E], RegisterDataType.UInt16, string.Empty,
                    ValueTransform.MoldedCasePreAlarmTime, FormatDescription: "见 6.8"),
                Number("预报警电流设定值 Ip", 0x001F, "A"),
            ]));

    /// <summary>按当前选择的设备类型取对应画像；未知类型抛出 <see cref="ArgumentOutOfRangeException"/>。</summary>
    /// <param name="deviceType">界面或服务传入的设备类型。</param>
    public static DeviceProfile Get(DeviceType deviceType) => deviceType switch
    {
        DeviceType.FrameController => FrameController,
        DeviceType.MoldedCaseCircuitBreaker => MoldedCaseCircuitBreaker,
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, null),
    };

    /// <summary>
    /// 塑壳目录里「单地址 UInt16 + 倍率」字段的快捷构造。
    /// <paramref name="multiplier"/> 为 1 时按原码显示（说明 ×1）；
    /// 为 0.02 时按协议把寄存器值当 1/50 秒（说明 ÷50），用于故障时间等。
    /// 码表类字段不要走这里，应直接 <see cref="RegisterDefinition"/> 并指定对应 <see cref="ValueTransform"/>。
    /// </summary>
    private static RegisterDefinition Number(
        string name,
        ushort address,
        string unit,
        decimal multiplier = 1m) =>
        new(name, [address], RegisterDataType.UInt16, unit, ValueTransform.Multiply,
            multiplier, multiplier == 1m ? "×1" : "÷50");
}
