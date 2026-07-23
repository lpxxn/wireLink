using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.Tests;

public sealed class ParserTests
{
    private static RawRegisterSample Sample(ushort address,ushort value)=>new(address,value,DateTimeOffset.UnixEpoch);

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
        var value=new RegisterParser().Parse([alarm],new Dictionary<ushort,RawRegisterSample>{{514,Sample(514,1)},{513,Sample(513,0)}},WordOrder.HighWordFirst).Single();
        Assert.Contains("DI输入1",value.Value);
    }

    [Fact]
    public void Current_uses_hidden_ratio_register()
    {
        var current=RegisterCatalog.DeviceDefinitions.Single(x=>x.Name=="A 相电流");
        var value=new RegisterParser().Parse([current],new Dictionary<ushort,RawRegisterSample>{{268,Sample(268,20)},{788,Sample(788,3)}},WordOrder.HighWordFirst).Single();
        Assert.Equal("60",value.Value); Assert.Contains("20 × 电流变比(3)",value.Formula);
    }

    [Fact]
    public void Percent_divides_by_100_and_keeps_two_decimals()
    {
        var definition=new RegisterDefinition("百分比",[20],RegisterDataType.UInt16,"%",ValueTransform.Percent,FormatDescription:"÷100");
        var value=new RegisterParser().Parse([definition],new Dictionary<ushort,RawRegisterSample>{{20,Sample(20,1234)}},WordOrder.HighWordFirst).Single();
        Assert.Equal("12.34",value.Value); Assert.Equal("1234 ÷ 100",value.Formula);
    }

    [Fact]
    public void Unknown_scale_keeps_raw_value_and_warning()
    {
        var definition=new RegisterDefinition("待确认倍率",[20],RegisterDataType.UInt16,"A",ValueTransform.RawUnconfirmed,FormatDescription:"×1 或 ×2 注1",ProtocolConfirmed:false);
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
