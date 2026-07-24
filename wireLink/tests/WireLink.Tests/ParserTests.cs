using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.Tests;

public sealed class ParserTests
{
    private static RawRegisterSample Sample(ushort address,ushort value)=>new(address,value,DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(BreakerSeries.BW1,10,1)]
    [InlineData(BreakerSeries.BW1,11,2)]
    [InlineData(BreakerSeries.BW3,11,1)]
    [InlineData(BreakerSeries.BW3,12,2)]
    public void Current_ratio_rule_matches_bw_thresholds(BreakerSeries series,byte ordinal,ushort expected)
    {
        Assert.Equal(expected,CurrentRatioRule.Calculate(series,ordinal));
    }

    [Theory]
    [InlineData(BreakerSeries.BW1,4,630)]
    [InlineData(BreakerSeries.BW1,11,2002)]
    [InlineData(BreakerSeries.BW3,11,2500)]
    [InlineData(BreakerSeries.BW3,21,4000)]
    public void Rated_current_is_mapped_from_controller_and_ordinal(
        BreakerSeries series,byte ordinal,ushort expected)
    {
        Assert.Equal(expected,CurrentRatioRule.GetRatedCurrent(series,ordinal));
    }

    [Theory]
    [InlineData(WordOrder.HighWordFirst,"305419896")]
    [InlineData(WordOrder.LowWordFirst,"1450709556")]
    public void Uint32_word_order_is_switchable(WordOrder order,string expected)
    {
        var definition=new RegisterDefinition("测试",[10,11],RegisterDataType.UInt32,"",ValueTransform.Multiply,1,"×1",false);
        var value=new RegisterParser().Parse([definition],new Dictionary<ushort,RawRegisterSample>{{10,Sample(10,0x1234)},{11,Sample(11,0x5678)}},order).Single();
        Assert.Equal(expected,value.Value); Assert.Equal(ParseStatus.ProtocolUnconfirmed,value.Status);
    }

    [Fact]
    public void Non_increasing_address_pair_preserves_protocol_order()
    {
        var alarm=RegisterCatalog.DeviceDefinitions.Single(x=>x.Name=="当前报警");
        Assert.Equal([514,513],alarm.Addresses);
        Assert.True(alarm.ProtocolConfirmed);
        var value=new RegisterParser().Parse([alarm],new Dictionary<ushort,RawRegisterSample>{{514,Sample(514,1)},{513,Sample(513,0)}},WordOrder.HighWordFirst).Single();
        Assert.Contains("DI输入1",value.Value);
        Assert.Equal(ParseStatus.Success,value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Current_uses_controller_and_hidden_rated_current_ordinal()
    {
        var current=RegisterCatalog.DeviceDefinitions.Single(x=>x.Name=="A 相电流");
        var value=new RegisterParser().Parse(
            [current],
            new Dictionary<ushort,RawRegisterSample>{{268,Sample(268,20)},{787,Sample(787,11)}},
            WordOrder.HighWordFirst,
            controllerSeries:BreakerSeries.BW1).Single();
        Assert.Equal("40",value.Value);
        Assert.Contains("20 × 电流变比(×2；BW1 序值 11=2002A)",value.Formula);
    }

    [Fact]
    public void Percent_uses_raw_value_and_appends_percent_sign()
    {
        var definition=new RegisterDefinition("百分比",[20],RegisterDataType.UInt16,"%",ValueTransform.Percent,FormatDescription:"原值直接显示");
        var value=new RegisterParser().Parse([definition],new Dictionary<ushort,RawRegisterSample>{{20,Sample(20,1234)}},WordOrder.HighWordFirst).Single();
        Assert.Equal("1234",value.Value); Assert.Equal("1234%",value.DisplayValue);
        Assert.Equal("百分比原值直接显示",value.Formula);
    }

    [Fact]
    public void Invalid_rated_current_ordinal_is_rejected()
    {
        var current=RegisterCatalog.DeviceDefinitions.Single(x=>x.Name=="A 相电流");
        var value=new RegisterParser().Parse(
            [current],
            new Dictionary<ushort,RawRegisterSample>{{268,Sample(268,20)},{787,Sample(787,24)}},
            WordOrder.HighWordFirst,
            controllerSeries:BreakerSeries.BW1).Single();
        Assert.Equal(ParseStatus.InvalidData,value.Status);
        Assert.Contains("0～23",value.Warning);
    }

    [Fact]
    public void Fault_data_zero_uses_current_ratio_for_overload()
    {
        var definition=RegisterCatalog.FaultDefinitions.Single(x=>x.Name=="故障数据 0");
        var samples=new Dictionary<ushort,RawRegisterSample>
        {
            {771,Sample(771,0x0700)}, {772,Sample(772,125)}, {787,Sample(787,11)},
        };
        var value=new RegisterParser().Parse(
            [definition],samples,WordOrder.HighWordFirst,FaultRecordType.Fault,BreakerSeries.BW1).Single();
        Assert.Equal("250 A",value.DisplayValue); Assert.Equal(ParseStatus.Success,value.Status);
    }

    [Fact]
    public void Record_selector_decodes_low_type_and_high_record_number_without_warning()
    {
        var definition=RegisterCatalog.FaultDefinitions.Single(x=>x.Name=="指定读取的记录");
        var value=new RegisterParser().Parse([definition],new Dictionary<ushort,RawRegisterSample>
        {
            {785,Sample(785,0x0301)},
        },WordOrder.HighWordFirst).Single();
        Assert.Equal("报警 / 记录 3",value.Value);
        Assert.Equal("L=类型，H=记录编号",value.Formula);
        Assert.Equal(ParseStatus.Success,value.Status);
        Assert.Null(value.Warning);
    }

    [Fact]
    public void Fault_trip_flag_takes_priority_when_alarm_flag_is_also_set()
    {
        var definitions=RegisterCatalog.DeviceDefinitions
            .Where(x=>x.Name is "当前故障/报警相别和类型" or "当前故障数据 0")
            .ToArray();
        var samples=new Dictionary<ushort,RawRegisterSample>
        {
            {512,Sample(512,(1<<2)|(1<<3))},
            {515,Sample(515,0x0700)},
            {516,Sample(516,125)},
            {787,Sample(787,11)},
        };
        var values=new RegisterParser().Parse(
            definitions,samples,WordOrder.HighWordFirst,controllerSeries:BreakerSeries.BW1);
        Assert.Contains("过载故障",values.Single(x=>x.Name=="当前故障/报警相别和类型").Value);
        Assert.Equal("250 A",values.Single(x=>x.Name=="当前故障数据 0").DisplayValue);
        Assert.All(values,value=>Assert.Equal(ParseStatus.Success,value.Status));
    }

    [Fact]
    public void State_change_record_uses_same_event_register_area_without_structure_warning()
    {
        var definitions=RegisterCatalog.FaultDefinitions
            .Where(x=>x.Name is "故障记录相别和类型" or "故障数据 0")
            .ToArray();
        var samples=new Dictionary<ushort,RawRegisterSample>
        {
            {771,Sample(771,0x0300)}, {772,Sample(772,0x1234)},
        };
        var values=new RegisterParser().Parse(definitions,samples,WordOrder.HighWordFirst,FaultRecordType.StateChange);
        Assert.All(values,value=>Assert.Equal(ParseStatus.Success,value.Status));
        Assert.Equal("0x1234",values.Single(x=>x.Name=="故障数据 0").Value);
        Assert.DoesNotContain(values,value=>value.Warning?.Contains("数据结构")==true);
    }

    [Fact]
    public void Fault_page_rated_current_displays_mapped_value()
    {
        var definition=RegisterCatalog.FaultDefinitions.Single(x=>x.Name=="额定电流");
        var value=new RegisterParser().Parse(
            [definition],
            new Dictionary<ushort,RawRegisterSample>{{787,Sample(787,4)}},
            WordOrder.HighWordFirst,
            controllerSeries:BreakerSeries.BW3).Single();
        Assert.Equal("630 A",value.DisplayValue);
        Assert.Contains("BW3 额定电流序值 4",value.Formula);
    }

    [Fact]
    public void Unknown_scale_keeps_raw_value_and_warning()
    {
        var definition=new RegisterDefinition("未定义事件字段",[20],RegisterDataType.UInt16,"",ValueTransform.RawUnconfirmed,FormatDescription:"待协议补充",ProtocolConfirmed:false);
        var value=new RegisterParser().Parse([definition],new Dictionary<ushort,RawRegisterSample>{{20,Sample(20,0x1234)}},WordOrder.HighWordFirst).Single();
        Assert.Equal("0x1234",value.Value); Assert.Equal(ParseStatus.ProtocolUnconfirmed,value.Status); Assert.NotNull(value.Warning);
    }

    [Fact]
    public void Invalid_bcd_is_local_parse_warning()
    {
        var definition=RegisterCatalog.FaultDefinitions[0];
        var value=new RegisterParser().Parse([definition],new Dictionary<ushort,RawRegisterSample>{{768,Sample(768,0x2A13)}},WordOrder.HighWordFirst).Single();
        Assert.Equal(ParseStatus.InvalidData,value.Status);
    }

    [Fact]
    public void Continuation_registers_do_not_create_duplicate_items()
    {
        Assert.Single(RegisterCatalog.DeviceDefinitions,x=>x.Addresses.Contains((ushort)336) || x.Addresses.Contains((ushort)337));
        Assert.DoesNotContain(RegisterCatalog.DeviceDefinitions,x=>x.Name==string.Empty);
    }
}
