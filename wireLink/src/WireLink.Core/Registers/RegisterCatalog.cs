using WireLink.Core.Models;

namespace WireLink.Core.Registers;

/// <summary>
/// 首版需要展示的寄存器目录。续寄存器只出现在 Addresses 中，
/// 因此不会在界面或 Excel 里生成重复字段。
/// </summary>
public static class RegisterCatalog
{
    public static IReadOnlyList<RegisterBlock> DeviceBlocks { get; } =
    [
        new(256, 3),
        new(268, 3),
        new(336, 8),
        new(352, 6),
        new(432, 2),
        new(512, 12),
        // 787 返回额定电流序值；它只用于计算设备页电流，不在设备页单独显示。
        new(787, 1),
    ];

    public static IReadOnlyList<RegisterDefinition> DeviceDefinitions { get; } =
    [
        Number("A 相电压", 256, "V"),
        Number("B 相电压", 257, "V"),
        Number("C 相电压", 258, "V"),
        Current("A 相电流", 268),
        Current("B 相电流", 269),
        Current("C 相电流", 270),
        UInt32("高精度电流测量 Ia", 336, 337, "A", 0.01m),
        UInt32("高精度电流测量 Ib", 338, 339, "A", 0.01m),
        UInt32("高精度电流测量 Ic", 340, 341, "A", 0.01m),
        UInt32("高精度电流测量 In", 342, 343, "A", 0.01m),
        UInt32("高精度电压测量 Uan", 352, 353, "V", 0.1m),
        UInt32("高精度电压测量 Ubn", 354, 355, "V", 0.1m),
        UInt32("高精度电压测量 Ucn", 356, 357, "V", 0.1m),
        UInt32("总有功电能", 432, 433, "kWh", 0.001m),
        new("运行状态", [512], RegisterDataType.UInt16, string.Empty, ValueTransform.RunStatus, FormatDescription: "见 5.2"),
        // 协议表按 514、513 的顺序定义当前报警；前者为高 16 位，后者为低 16 位，不能按地址重新排序。
        new("当前报警", [514, 513], RegisterDataType.UInt32, string.Empty, ValueTransform.AlarmBits, FormatDescription: "见 5.3"),
        new("当前故障/报警相别和类型", [515], RegisterDataType.UInt16, string.Empty, ValueTransform.CurrentEvent, FormatDescription: "见 5.4"),
        new("当前故障数据 0", [516], RegisterDataType.UInt16, string.Empty, ValueTransform.EventData0, FormatDescription: "见 5.5；按事件类型解析"),
        EventAdditional("当前故障数据 1", 517),
        EventAdditional("当前故障数据 2", 518),
        EventAdditional("当前故障数据 3", 519),
        EventAdditional("当前故障数据 4", 520),
        EventAdditional("当前故障数据 5", 521),
        EventAdditional("当前故障数据 6", 522),
        EventAdditional("当前故障数据 7", 523),
    ];

    public static IReadOnlyList<RegisterDefinition> FaultDefinitions { get; } =
    [
        new("故障记录年月", [768], RegisterDataType.UInt16, string.Empty, ValueTransform.BcdYearMonth, FormatDescription: "BCD：L月/H年"),
        new("故障记录日时", [769], RegisterDataType.UInt16, string.Empty, ValueTransform.BcdDayHour, FormatDescription: "BCD：L时/H日"),
        new("故障记录分秒", [770], RegisterDataType.UInt16, string.Empty, ValueTransform.BcdMinuteSecond, FormatDescription: "BCD：L秒/H分"),
        new("故障记录相别和类型", [771], RegisterDataType.UInt16, string.Empty, ValueTransform.CurrentEvent, FormatDescription: "见 5.4"),
        new("故障数据 0", [772], RegisterDataType.UInt16, string.Empty, ValueTransform.EventData0, FormatDescription: "见 5.5；按事件类型解析"),
        EventAdditional("故障数据 1", 773),
        EventAdditional("故障数据 2", 774),
        EventAdditional("故障数据 3", 775),
        EventAdditional("故障数据 4", 776),
        EventAdditional("故障数据 5", 777),
        EventAdditional("故障数据 6", 778),
        EventAdditional("故障数据 7", 779),
        new("本次上电年月", [780], RegisterDataType.UInt16, string.Empty, ValueTransform.BcdYearMonth, FormatDescription: "BCD：L月/H年"),
        new("本次上电日时", [781], RegisterDataType.UInt16, string.Empty, ValueTransform.BcdDayHour, FormatDescription: "BCD：L时/H日"),
        new("本次上电分秒", [782], RegisterDataType.UInt16, string.Empty, ValueTransform.BcdMinuteSecond, FormatDescription: "BCD：L秒/H分"),
        new("软件版本号", [783], RegisterDataType.UInt16, string.Empty, ValueTransform.Multiply, FormatDescription: "协议标注未使用"),
        new("故障记录状态标志", [784], RegisterDataType.UInt16, string.Empty, ValueTransform.FaultRecordStatus, FormatDescription: "见 5.6"),
        new("指定读取的记录", [785], RegisterDataType.UInt16, string.Empty, ValueTransform.RecordSelector, FormatDescription: "L类型/H序号"),
        Number("框架等级", 786, "A"),
        new("额定电流", [787], RegisterDataType.UInt16, "A", ValueTransform.RatedCurrent,
            FormatDescription: "按控制器系列和额定电流序值映射"),
    ];

    private static RegisterDefinition Number(string name, ushort address, string unit, decimal multiplier = 1m) =>
        new(name, [address], RegisterDataType.UInt16, unit, ValueTransform.Multiply, multiplier, $"×{multiplier}");

    private static RegisterDefinition Current(string name, ushort address) =>
        new(name, [address], RegisterDataType.UInt16, "A", ValueTransform.CurrentRatio, FormatDescription: "×电流变比");

    private static RegisterDefinition EventAdditional(string name, ushort address) =>
        new(name, [address], RegisterDataType.UInt16, string.Empty, ValueTransform.EventAdditionalData,
            FormatDescription: "故障按 5.5；报警时为空");

    private static RegisterDefinition UInt32(
        string name,
        ushort first,
        ushort second,
        string unit,
        decimal multiplier) =>
        new(name, [first, second], RegisterDataType.UInt32, unit, ValueTransform.Multiply, multiplier, $"×{multiplier}");
}
