using WireLink.Core.Models;

namespace WireLink.Core.Registers;

public sealed record RegisterPageProfile(
    IReadOnlyList<RegisterBlock> Blocks,
    IReadOnlyList<RegisterDefinition> Definitions);

public sealed record DeviceProfile(
    DeviceType DeviceType,
    string DisplayName,
    ushort ProbeRegister,
    RegisterPageProfile DeviceData,
    RegisterPageProfile? ProtectionData = null);

/// <summary>各设备类型的探测地址、读取区间和展示字段。</summary>
public static class DeviceProfileCatalog
{
    public static DeviceProfile FrameController { get; } = new(
        DeviceType.FrameController,
        "框架控制器",
        0x0100,
        new RegisterPageProfile(RegisterCatalog.DeviceBlocks, RegisterCatalog.DeviceDefinitions));

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

    public static DeviceProfile Get(DeviceType deviceType) => deviceType switch
    {
        DeviceType.FrameController => FrameController,
        DeviceType.MoldedCaseCircuitBreaker => MoldedCaseCircuitBreaker,
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, null),
    };

    private static RegisterDefinition Number(
        string name,
        ushort address,
        string unit,
        decimal multiplier = 1m) =>
        new(name, [address], RegisterDataType.UInt16, unit, ValueTransform.Multiply,
            multiplier, multiplier == 1m ? "×1" : "÷50");
}
