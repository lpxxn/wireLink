using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.Tests;

public sealed class ParserTests
{
    private static RawRegisterSample Sample(ushort address, ushort value) => new(address, value, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(BreakerSeries.BW1, 10, 1)]
    [InlineData(BreakerSeries.BW1, 11, 2)]
    [InlineData(BreakerSeries.BW3, 11, 1)]
    [InlineData(BreakerSeries.BW3, 12, 2)]
    public void Current_ratio_rule_matches_bw_thresholds(BreakerSeries series, byte ordinal, ushort expected)
    {
        Assert.Equal(expected, CurrentRatioRule.Calculate(series, ordinal));
    }

    [Theory]
    [InlineData(BreakerSeries.BW1, 4, 630)]
    [InlineData(BreakerSeries.BW1, 11, 2002)]
    [InlineData(BreakerSeries.BW3, 11, 2500)]
    [InlineData(BreakerSeries.BW3, 21, 4000)]
    public void Rated_current_is_mapped_from_controller_and_ordinal(
        BreakerSeries series, byte ordinal, ushort expected)
    {
        Assert.Equal(expected, CurrentRatioRule.GetRatedCurrent(series, ordinal));
    }

    [Theory]
    [InlineData(WordOrder.HighWordFirst, "305419896")]
    [InlineData(WordOrder.LowWordFirst, "1450709556")]
    public void Uint32_word_order_is_switchable(WordOrder order, string expected)
    {
        var definition = new RegisterDefinition("测试", [10, 11], RegisterDataType.UInt32, "", ValueTransform.Multiply, 1, "×1", false);
        var value = new RegisterParser().Parse([definition], new Dictionary<ushort, RawRegisterSample> { { 10, Sample(10, 0x1234) }, { 11, Sample(11, 0x5678) } }, order).Single();
        Assert.Equal(expected, value.Value); Assert.Equal(ParseStatus.ProtocolUnconfirmed, value.Status);
    }

    [Fact]
    public void Non_increasing_address_pair_preserves_protocol_order()
    {
        var alarm = RegisterCatalog.DeviceDefinitions.Single(x => x.Name == "当前报警");
        Assert.Equal([514, 513], alarm.Addresses);
        Assert.True(alarm.ProtocolConfirmed);
        var value = new RegisterParser().Parse([alarm], new Dictionary<ushort, RawRegisterSample> { { 514, Sample(514, 1) }, { 513, Sample(513, 0) } }, WordOrder.HighWordFirst).Single();
        Assert.Contains("DI输入1", value.Value);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Current_uses_controller_and_low_byte_of_register_1552()
    {
        var current = RegisterCatalog.DeviceDefinitions.Single(x => x.Name == "A 相电流");
        var value = new RegisterParser().Parse(
            [current],
            // COM5 实机曾返回 0x110F：高位非零，低 8 位序值为 15。
            new Dictionary<ushort, RawRegisterSample> { { 268, Sample(268, 20) }, { 1552, Sample(1552, 0x110F) } },
            WordOrder.HighWordFirst,
            controllerSeries: BreakerSeries.BW1).Single();
        Assert.Equal("40", value.Value);
        Assert.Contains("20 × 电流变比(×2；1552.bit0～bit7=15；BW1=3200A)", value.Formula);
    }

    [Fact]
    public void Percent_uses_raw_value_and_appends_percent_sign()
    {
        var definition = new RegisterDefinition("百分比", [20], RegisterDataType.UInt16, "%", ValueTransform.Percent, FormatDescription: "原值直接显示");
        var value = new RegisterParser().Parse([definition], new Dictionary<ushort, RawRegisterSample> { { 20, Sample(20, 1234) } }, WordOrder.HighWordFirst).Single();
        Assert.Equal("1234", value.Value); Assert.Equal("1234%", value.DisplayValue);
        Assert.Equal("百分比原值直接显示", value.Formula);
    }

    [Fact]
    public void Thermal_capacity_is_after_c_phase_current_and_uses_percent_display()
    {
        var definitions = RegisterCatalog.DeviceDefinitions;
        var currentIndex = definitions.ToList().FindIndex(x => x.Name == "C 相电流");
        var thermalIndex = definitions.ToList().FindIndex(x => x.Name == "当前热容");
        Assert.Equal(currentIndex + 1, thermalIndex);

        var definition = definitions[thermalIndex];
        Assert.Equal([(ushort)279], definition.Addresses);
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample> { { 279, Sample(279, 68) } },
            WordOrder.HighWordFirst).Single();
        Assert.Equal("68%", value.DisplayValue);
        Assert.Equal(ParseStatus.Success, value.Status);
    }

    [Fact]
    public void Invalid_rated_current_ordinal_is_rejected()
    {
        var current = RegisterCatalog.DeviceDefinitions.Single(x => x.Name == "A 相电流");
        var value = new RegisterParser().Parse(
            [current],
            new Dictionary<ushort, RawRegisterSample> { { 268, Sample(268, 20) }, { 1552, Sample(1552, 0x0318) } },
            WordOrder.HighWordFirst,
            controllerSeries: BreakerSeries.BW1).Single();
        Assert.Equal(ParseStatus.InvalidData, value.Status);
        Assert.Contains("0～23", value.Warning);
    }

    [Fact]
    public void Fault_data_zero_uses_current_ratio_for_overload()
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Name == "故障数据 0");
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            {771,Sample(771,0x0700)}, {772,Sample(772,125)}, {1552,Sample(1552,0x030B)},
        };
        var value = new RegisterParser().Parse(
            [definition], samples, WordOrder.HighWordFirst, FaultRecordType.Fault, BreakerSeries.BW1).Single();
        Assert.Equal("250 A", value.DisplayValue); Assert.Equal(ParseStatus.Success, value.Status);
    }

    [Fact]
    public void Record_selector_decodes_low_type_and_high_record_number_without_warning()
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Name == "指定读取的记录");
        var value = new RegisterParser().Parse([definition], new Dictionary<ushort, RawRegisterSample>
        {
            {785,Sample(785,0x0301)},
        }, WordOrder.HighWordFirst).Single();
        Assert.Equal("报警 / 第 3 条记录", value.Value);
        Assert.Equal("L=记录类型，H=第几条记录", value.Formula);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Fault_trip_flag_takes_priority_when_alarm_flag_is_also_set()
    {
        var definitions = RegisterCatalog.DeviceDefinitions
            .Where(x => x.Name is "当前故障/报警相别和类型" or "当前故障数据 0")
            .ToArray();
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            {512,Sample(512,(1<<2)|(1<<3))},
            {515,Sample(515,0x0700)},
            {516,Sample(516,125)},
            {1552,Sample(1552,0x030B)},
        };
        var values = new RegisterParser().Parse(
            definitions, samples, WordOrder.HighWordFirst, controllerSeries: BreakerSeries.BW1);
        Assert.Contains("过载故障", values.Single(x => x.Name == "当前故障/报警相别和类型").Value);
        Assert.Equal("250 A", values.Single(x => x.Name == "当前故障数据 0").DisplayValue);
        Assert.All(values, value => Assert.Equal(ParseStatus.Success, value.Status));
    }

    [Fact]
    public void Current_alarm_only_data_zero_is_valid()
    {
        var definition = RegisterCatalog.DeviceDefinitions.Single(x => x.Addresses.Contains((ushort)517));
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample>
            {
                {512,Sample(512,1<<2)},
                {517,Sample(517,0x1234)},
            },
            WordOrder.HighWordFirst).Single();
        Assert.Equal(string.Empty, value.Value);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Equal("报警仅数据 0 有效，本字段为空", value.Formula);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Historical_alarm_only_data_zero_is_valid()
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Addresses.Contains((ushort)773));
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample> { { 773, Sample(773, 0x5678) } },
            WordOrder.HighWordFirst,
            FaultRecordType.Alarm).Single();
        Assert.Equal(string.Empty, value.Value);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Historical_alarm_with_undefined_type_displays_data_zero_as_decimal_raw_value()
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Name == "故障数据 0");
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            {771,Sample(771,0x1601)},
            {772,Sample(772,0x00C8)},
        };

        var value = new RegisterParser().Parse(
            [definition], samples, WordOrder.HighWordFirst, FaultRecordType.Alarm).Single();

        Assert.Equal("200", value.Value);
        Assert.Contains("协议未定义报警类型码 22", value.Formula);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Fault_additional_data_displays_decimal_raw_value_without_warning()
    {
        var definition = RegisterCatalog.DeviceDefinitions.Single(x => x.Addresses.Contains((ushort)517));
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample>
            {
                {512,Sample(512,1<<3)},
                {517,Sample(517,0x1234)},
            },
            WordOrder.HighWordFirst).Single();
        Assert.Equal("4660", value.Value);
        Assert.Equal("事件数据原始值直接显示", value.Formula);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void No_current_fault_or_alarm_has_no_protocol_warnings()
    {
        var definitions = RegisterCatalog.DeviceDefinitions
            .Where(x => x.Addresses.Any(address => address is >= 515 and <= 523))
            .ToArray();
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            {512,Sample(512,0x0002)},
        };
        for (ushort address = 515; address <= 523; address++)
            samples[address] = Sample(address, 0);

        var values = new RegisterParser().Parse(definitions, samples, WordOrder.HighWordFirst);
        Assert.Equal("无当前故障/报警",
            values.Single(x => x.Addresses.Contains((ushort)515)).Value);
        Assert.All(values.Where(x => !x.Addresses.Contains((ushort)515)), value =>
        {
            Assert.Equal(string.Empty, value.Value);
            Assert.Equal(ParseStatus.Success, value.Status);
            Assert.Null(value.Warning);
        });
        Assert.All(values, value => Assert.Equal(ParseStatus.Success, value.Status));
    }

    [Fact]
    public void Current_fault_data_three_displays_decimal_raw_value()
    {
        var definition = RegisterCatalog.DeviceDefinitions.Single(x => x.Addresses.Contains((ushort)519));
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample>
            {
                {512,Sample(512,1<<3)},
                {519,Sample(519,0x1234)},
            },
            WordOrder.HighWordFirst).Single();
        Assert.Equal("4660", value.Value);
        Assert.Equal("故障数据 3 原始值直接显示", value.Formula);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Historical_fault_data_three_displays_decimal_raw_value()
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Addresses.Contains((ushort)775));
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample> { { 775, Sample(775, 1234) } },
            WordOrder.HighWordFirst,
            FaultRecordType.Fault).Single();
        Assert.Equal("1234", value.Value);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void State_change_record_uses_same_event_register_area_without_structure_warning()
    {
        var definitions = RegisterCatalog.FaultDefinitions
            .Where(x => x.Name is "故障记录相别和类型" or "故障数据 0")
            .ToArray();
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            {771,Sample(771,0x0300)}, {772,Sample(772,0x1234)},
        };
        var values = new RegisterParser().Parse(definitions, samples, WordOrder.HighWordFirst, FaultRecordType.StateChange);
        Assert.All(values, value => Assert.Equal(ParseStatus.Success, value.Status));
        Assert.Equal("A相 / 本地合闸", values.Single(x => x.Name == "故障记录相别和类型").Value);
        Assert.Equal("4660", values.Single(x => x.Name == "故障数据 0").Value);
        Assert.DoesNotContain(values, value => value.Warning?.Contains("数据结构") == true);
    }

    [Theory]
    [InlineData(0, "无变位")]
    [InlineData(3, "本地合闸")]
    [InlineData(4, "本地分闸")]
    [InlineData(5, "故障跳闸")]
    public void Known_state_change_type_displays_confirmed_description(int typeCode, string description)
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Name == "故障记录相别和类型");
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample> { { 771, Sample(771, (ushort)(typeCode << 8)) } },
            WordOrder.HighWordFirst,
            FaultRecordType.StateChange).Single();

        Assert.Equal($"A相 / {description}", value.Value);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Unconfirmed_state_change_type_keeps_raw_type_code(int typeCode)
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Name == "故障记录相别和类型");
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample> { { 771, Sample(771, (ushort)(typeCode << 8)) } },
            WordOrder.HighWordFirst,
            FaultRecordType.StateChange).Single();

        Assert.Equal($"A相 / 未知变位类型 {typeCode}", value.Value);
        Assert.Equal(ParseStatus.ProtocolUnconfirmed, value.Status);
        Assert.Contains("中文含义尚未确认", value.Warning);
    }

    [Fact]
    public void Fault_page_rated_current_displays_mapped_value()
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Name == "额定电流");
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample> { { 1552, Sample(1552, 0x0F04) } },
            WordOrder.HighWordFirst,
            controllerSeries: BreakerSeries.BW3).Single();
        Assert.Equal("630 A", value.DisplayValue);
        Assert.Contains("1552=0x0F04；bit0～bit7=4；BW3", value.Formula);
    }

    [Fact]
    public void Unknown_scale_keeps_raw_value_and_warning()
    {
        var definition = new RegisterDefinition("未定义事件字段", [20], RegisterDataType.UInt16, "", ValueTransform.RawUnconfirmed, FormatDescription: "待协议补充", ProtocolConfirmed: false);
        var value = new RegisterParser().Parse([definition], new Dictionary<ushort, RawRegisterSample> { { 20, Sample(20, 0x1234) } }, WordOrder.HighWordFirst).Single();
        Assert.Equal("0x1234", value.Value); Assert.Equal(ParseStatus.ProtocolUnconfirmed, value.Status); Assert.NotNull(value.Warning);
    }

    [Fact]
    public void Invalid_bcd_is_local_parse_warning()
    {
        var trace = new RecordingProtocolTrace();
        var definition = RegisterCatalog.FaultDefinitions[0];
        var value = new RegisterParser(trace).Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample>
            {
                {768,Sample(768,0x2A13)},
                {769,Sample(769,0x2214)},
                {770,Sample(770,0x3000)},
            },
            WordOrder.HighWordFirst).Single();
        Assert.Equal(ParseStatus.InvalidData, value.Status);
        var error = Assert.Single(trace.ErrorMessages);
        Assert.Contains("寄存器地址=768(0x0300), 769(0x0301), 770(0x0302)", error.Message);
        Assert.Contains("768(0x0300)=0x2A13", error.Message);
        Assert.NotNull(error.Exception);
    }

    [Fact]
    public void Missing_register_is_logged_with_expected_and_missing_addresses()
    {
        var trace = new RecordingProtocolTrace();
        var definition = RegisterCatalog.DeviceDefinitions.Single(x => x.Name == "高精度电流测量 Ia");

        var values = new RegisterParser(trace).Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample> { { 336, Sample(336, 1) } },
            WordOrder.HighWordFirst);

        Assert.Empty(values);
        var warning = Assert.Single(trace.WarningMessages);
        Assert.Contains("字段=高精度电流测量 Ia", warning);
        Assert.Contains("寄存器地址=336(0x0150), 337(0x0151)", warning);
        Assert.Contains("缺失地址=337(0x0151)", warning);
    }

    [Fact]
    public void Fault_time_combines_three_bcd_registers()
    {
        var definition = RegisterCatalog.FaultDefinitions.Single(x => x.Name == "故障记录时间");
        var value = new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort, RawRegisterSample>
            {
                {768,Sample(768,0x2607)},
                {769,Sample(769,0x2214)},
                {770,Sample(770,0x3000)},
            },
            WordOrder.HighWordFirst).Single();
        Assert.Equal("2026-07-22 14:30:00", value.Value);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Theory]
    [InlineData(ValueTransform.MoldedCasePhase, 0, "A相")]
    [InlineData(ValueTransform.MoldedCasePhase, 3, "N相")]
    [InlineData(ValueTransform.MoldedCaseFaultType, 0, "无故障")]
    [InlineData(ValueTransform.MoldedCaseFaultType, 1, "瞬时故障")]
    [InlineData(ValueTransform.MoldedCaseFaultType, 2, "漏电故障")]
    [InlineData(ValueTransform.MoldedCaseFaultType, 4, "接地故障")]
    [InlineData(ValueTransform.MoldedCaseFaultType, 8, "短延时故障")]
    [InlineData(ValueTransform.MoldedCaseFaultType, 16, "长延时故障")]
    [InlineData(ValueTransform.MoldedCaseFaultType, 48, "故障未读取")]
    [InlineData(ValueTransform.MoldedCaseLongDelayTime, 0, "OFF")]
    [InlineData(ValueTransform.MoldedCaseLongDelayTime, 150, "150 s")]
    [InlineData(ValueTransform.MoldedCaseShortDelayTime, 0, "OFF")]
    [InlineData(ValueTransform.MoldedCaseShortDelayTime, 3, "0.06 s")]
    [InlineData(ValueTransform.MoldedCaseShortDelayTime, 15, "0.3 s")]
    [InlineData(ValueTransform.MoldedCaseGroundTime, 0, "0.1 s")]
    [InlineData(ValueTransform.MoldedCaseGroundTime, 7, "0.8 s")]
    [InlineData(ValueTransform.MoldedCaseGroundTime, 8, "报警")]
    [InlineData(ValueTransform.MoldedCasePreAlarmTime, 0, "0.1 s")]
    [InlineData(ValueTransform.MoldedCasePreAlarmTime, 9, "1.0 s")]
    public void Molded_case_parser_maps_confirmed_values(
        ValueTransform transform, int raw, string expected)
    {
        var definition = new RegisterDefinition("测试", [1], RegisterDataType.UInt16, string.Empty, transform);
        var value = new RegisterParser().Parse(
            [definition], new Dictionary<ushort, RawRegisterSample> { { 1, Sample(1, (ushort)raw) } },
            WordOrder.HighWordFirst).Single();

        Assert.Equal(expected, value.DisplayValue);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Null(value.Warning);
    }

    [Theory]
    [InlineData(ValueTransform.MoldedCasePhase, 4)]
    [InlineData(ValueTransform.MoldedCaseFaultType, 3)]
    [InlineData(ValueTransform.MoldedCaseLongDelayTime, 151)]
    [InlineData(ValueTransform.MoldedCaseShortDelayTime, 4)]
    [InlineData(ValueTransform.MoldedCaseGroundTime, 9)]
    [InlineData(ValueTransform.MoldedCasePreAlarmTime, 10)]
    public void Molded_case_parser_keeps_unknown_raw_value_with_warning(
        ValueTransform transform, int raw)
    {
        var definition = new RegisterDefinition("测试", [1], RegisterDataType.UInt16, string.Empty, transform);
        var value = new RegisterParser().Parse(
            [definition], new Dictionary<ushort, RawRegisterSample> { { 1, Sample(1, (ushort)raw) } },
            WordOrder.HighWordFirst).Single();

        Assert.Equal(raw.ToString(), value.Value);
        Assert.Equal(ParseStatus.ProtocolUnconfirmed, value.Status);
        Assert.Contains("协议未定义", value.Warning);
    }

    [Fact]
    public void Molded_case_001d_displays_raw_decimal_value()
    {
        var definition = DeviceProfileCatalog.MoldedCaseCircuitBreaker.ProtectionData!.Definitions
            .Single(value => value.Addresses.Contains((ushort)0x001D));

        var value = new RegisterParser().Parse(
            [definition], new Dictionary<ushort, RawRegisterSample>
            {
                [0x001D] = Sample(0x001D, 1234),
            }, WordOrder.HighWordFirst).Single();

        Assert.Equal("1234", value.DisplayValue);
        Assert.Single(value.RawSamples);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.True(definition.IsReadable);
        Assert.Equal(string.Empty, definition.Unit);
    }

    [Theory]
    [InlineData(1, "2.50 A")]
    [InlineData(3, "250 A")]
    public void Frame_protection_current_uses_mode_from_1793(int mode, string expected)
    {
        var definition = DeviceProfileCatalog.FrameController.ProtectionData!.Definitions
            .Single(value => value.Name == "报警启动值");
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            [1296] = Sample(1296, 250),
            [1552] = Sample(1552, 0x0204),
            [1793] = Sample(1793, (ushort)(mode << 10)),
        };

        var value = new RegisterParser().Parse(
            [definition], samples, WordOrder.HighWordFirst, controllerSeries: BreakerSeries.BW1).Single();

        Assert.Equal(expected, value.DisplayValue);
        Assert.Equal(ParseStatus.Success, value.Status);
        Assert.Contains($"bit12～bit10={mode}", value.Formula);
    }

    [Theory]
    [InlineData(1, 3, "0.17 s")]
    [InlineData(3, 25, "0.25 s")]
    public void Frame_protection_action_time_uses_leakage_table_or_ground_scale(
        int mode, int raw, string expected)
    {
        var definition = DeviceProfileCatalog.FrameController.ProtectionData!.Definitions
            .Single(value => value.Name == "保护动作时间");
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            [1288] = Sample(1288, (ushort)raw),
            [1793] = Sample(1793, (ushort)(mode << 10)),
        };

        var value = new RegisterParser().Parse([definition], samples, WordOrder.HighWordFirst).Single();

        Assert.Equal(expected, value.DisplayValue);
        Assert.Equal(ParseStatus.Success, value.Status);
    }

    [Fact]
    public void Frame_protection_mode_two_keeps_mode_dependent_value_unscaled()
    {
        var definition = DeviceProfileCatalog.FrameController.ProtectionData!.Definitions
            .Single(value => value.Name == "保护动作值");
        var samples = new Dictionary<ushort, RawRegisterSample>
        {
            [1287] = Sample(1287, 315),
            [1793] = Sample(1793, 2 << 10),
        };

        var value = new RegisterParser().Parse([definition], samples, WordOrder.HighWordFirst).Single();

        Assert.Equal("315", value.DisplayValue);
        Assert.Equal(ParseStatus.ProtocolUnconfirmed, value.Status);
        Assert.Contains("差值型", value.Warning);
    }

    [Theory]
    [InlineData(0, "50%")]
    [InlineData(1, "100%")]
    [InlineData(2, "160%")]
    [InlineData(3, "200%")]
    [InlineData(4, "关闭")]
    public void Frame_n_phase_protection_uses_5_14_table(int raw, string expected)
    {
        var definition = DeviceProfileCatalog.FrameController.ProtectionData!.Definitions
            .Single(value => value.Name == "N 相保护设置");
        var value = new RegisterParser().Parse(
            [definition], new Dictionary<ushort, RawRegisterSample> { { 1286, Sample(1286, (ushort)raw) } },
            WordOrder.HighWordFirst).Single();

        Assert.Equal(expected, value.DisplayValue);
        Assert.Equal(ParseStatus.Success, value.Status);
    }

    [Fact]
    public void Frame_current_imbalance_1299_splits_low_start_and_high_return_bytes()
    {
        var definitions = DeviceProfileCatalog.FrameController.ProtectionData!.Definitions
            .Where(value => value.Addresses.Contains((ushort)1299)).ToArray();
        var values = new RegisterParser().Parse(
            definitions, new Dictionary<ushort, RawRegisterSample> { { 1299, Sample(1299, 0x141E) } },
            WordOrder.HighWordFirst);

        Assert.Equal("30%", values.Single(value => value.Name == "I 不平衡启动值").DisplayValue);
        Assert.Equal("20%", values.Single(value => value.Name == "I 不平衡返回值").DisplayValue);
    }

    [Fact]
    public void Frame_alarm_time_1298_splits_low_start_and_high_return_bytes()
    {
        var definitions = DeviceProfileCatalog.FrameController.ProtectionData!.Definitions
            .Where(value => value.Addresses.Contains((ushort)1298)).ToArray();
        var values = new RegisterParser().Parse(
            definitions, new Dictionary<ushort, RawRegisterSample> { { 1298, Sample(1298, 0x141E) } },
            WordOrder.HighWordFirst);

        Assert.Equal("0.30 s", values.Single(value => value.Name == "报警启动时间").DisplayValue);
        Assert.Equal("0.20 s", values.Single(value => value.Name == "报警返回时间").DisplayValue);
        Assert.All(values, value => Assert.Equal(ParseStatus.Success, value.Status));
    }

    [Fact]
    public void Continuation_registers_do_not_create_duplicate_items()
    {
        Assert.Single(RegisterCatalog.DeviceDefinitions, x => x.Addresses.Contains((ushort)336) || x.Addresses.Contains((ushort)337));
        Assert.DoesNotContain(RegisterCatalog.DeviceDefinitions, x => x.Name == string.Empty);
    }
}
