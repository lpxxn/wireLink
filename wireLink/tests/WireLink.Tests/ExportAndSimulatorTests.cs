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
                [new RawRegisterSample((ushort)(256+i),0xABCD,DateTimeOffset.Now)],ParseStatus.Success,null,DateTimeOffset.Now)).ToArray();
            await new ClosedXmlExportService().ExportAsync(path,new ExcelExportContext("设备数据",values,DateTimeOffset.Now));
            using var book=new XLWorkbook(path); var sheet=book.Worksheet(1);
            Assert.Equal("名称",sheet.Cell(4,1).GetString()); Assert.Equal("计算值",sheet.Cell(4,4).GetString());
            var text=string.Join('|',sheet.CellsUsed().Select(c=>c.GetString()));
            Assert.DoesNotContain("256",text); Assert.DoesNotContain("ABCD",text);
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
}
