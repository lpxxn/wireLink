using ClosedXML.Excel;
using WireLink.Core.Models;
using WireLink.Core.Protocol;
using WireLink.Core.Services;
using WireLink.Infrastructure.Export;
using WireLink.Simulator;

namespace WireLink.Tests;

public sealed class ExportAndSimulatorTests
{
    [Fact]
    public async Task Excel_uses_four_columns_without_addresses_or_raw_values()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wirelink-{Guid.NewGuid():N}.xlsx");
        try
        {
            var values=Enumerable.Range(0,3).Select(i=>new DecodedValue($"字段{i}",[(ushort)(256+i)],$"值{i}","V","公式",
                [new RawRegisterSample((ushort)(256+i),0xABCD,DateTimeOffset.Now)],
                i==1?ParseStatus.InvalidData:ParseStatus.Success,
                i==1?"测试警告":null,
                DateTimeOffset.Now)).ToArray();
            await new ClosedXmlExportService().ExportAsync(path,new ExcelExportContext("设备数据",values,DateTimeOffset.Now));
            using var book=new XLWorkbook(path); var sheet=book.Worksheet(1);
            Assert.Equal("名称",sheet.Cell(4,1).GetString()); Assert.Equal("计算值",sheet.Cell(4,4).GetString());
            var text=string.Join('|',sheet.CellsUsed().Select(c=>c.GetString()));
            Assert.DoesNotContain("256",text); Assert.DoesNotContain("ABCD",text);
            Assert.Empty(sheet.MergedRanges);
            Assert.All(sheet.CellsUsed(),cell=>
            {
                Assert.False(cell.Style.Font.Bold);
                Assert.Equal(XLFillPatternValues.None,cell.Style.Fill.PatternType);
                Assert.Equal(XLBorderStyleValues.None,cell.Style.Border.TopBorder);
            });
        }
        finally { if(File.Exists(path))File.Delete(path); }
    }

    [Fact]
    public void Simulator_supports_03_and_06_with_crc()
    {
        var engine=new SimulatorEngine(1);
        var read=Crc16Modbus.Append([1,3,1,0,0,1]);
        var response=engine.Process(read)!;
        Assert.True(Crc16Modbus.IsValid(response)); Assert.Equal(230,(response[3]<<8)|response[4]);
        var thermalResponse=engine.Process(Crc16Modbus.Append([1,3,1,23,0,1]))!;
        Assert.Equal(68,(thermalResponse[3]<<8)|thermalResponse[4]);
        var operationResponse=engine.Process(Crc16Modbus.Append([1,3,4,7,0,1]))!;
        Assert.Equal(128,(operationResponse[3]<<8)|operationResponse[4]);
        var ratedCurrentConfigurationResponse=engine.Process(Crc16Modbus.Append([1,3,6,16,0,1]))!;
        Assert.Equal(0x0304,(ratedCurrentConfigurationResponse[3]<<8)|ratedCurrentConfigurationResponse[4]);
        var write=Crc16Modbus.Append([1,6,3,17,2,1]);
        var echo=engine.Process(write)!;
        Assert.Equal(write,echo);
    }

    [Fact]
    public void Simulator_can_inject_bad_crc_once()
    {
        var engine=new SimulatorEngine(1){FaultMode=SimulatorFaultMode.BadCrcOnce};
        var request=Crc16Modbus.Append([1,3,1,0,0,1]);
        Assert.False(Crc16Modbus.IsValid(engine.Process(request)!));
        Assert.True(Crc16Modbus.IsValid(engine.Process(request)!));
    }

    [Fact]
    public void Simulator_can_switch_current_fault_and_alarm_registers()
    {
        var engine=new SimulatorEngine(1);

        engine.SetCurrentEvent(SimulatorCurrentEventMode.Fault);
        var fault=ReadRegisters(engine,512,12);
        Assert.Equal(0x0002 | (1 << 3) | (1 << 10),fault[0]);
        Assert.Equal(0x0700,fault[3]);
        Assert.Equal(125,fault[4]);

        engine.SetCurrentEvent(SimulatorCurrentEventMode.Alarm);
        var alarm=ReadRegisters(engine,512,12);
        Assert.Equal(0x0002 | (1 << 2) | (1 << 11),alarm[0]);
        Assert.Equal(1 << 2,alarm[1]);
        Assert.Equal(0x0300,alarm[3]);
        Assert.Equal(125,alarm[4]);

        engine.SetCurrentEvent(SimulatorCurrentEventMode.Normal);
        var normal=ReadRegisters(engine,512,12);
        Assert.Equal(0x0002,normal[0]);
        Assert.All(normal.Skip(1),value=>Assert.Equal(0,value));
    }

    private static ushort[] ReadRegisters(SimulatorEngine engine, ushort start, ushort count)
    {
        var request=Crc16Modbus.Append([
            (byte)1, (byte)3,
            (byte)(start >> 8), (byte)start,
            (byte)(count >> 8), (byte)count]);
        var response=engine.Process(request)!;
        Assert.True(Crc16Modbus.IsValid(response));
        return Enumerable.Range(0,count)
            .Select(i=>(ushort)((response[3 + i * 2] << 8) | response[4 + i * 2]))
            .ToArray();
    }
}
