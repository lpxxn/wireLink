using ClosedXML.Excel;
using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;
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
        var ordinalResponse=engine.Process(Crc16Modbus.Append([1,3,3,19,0,1]))!;
        Assert.Equal(4,(ordinalResponse[3]<<8)|ordinalResponse[4]);
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
    public void Simulator_exposes_all_18_waveform_blocks_but_not_address_gaps()
    {
        var engine=new SimulatorEngine(1);
        Assert.Equal(WaveformCatalog.TotalBlocks,PdfWaveformSampleCatalog.Frames.Count);
        Assert.Equal(
            WaveformCatalog.Blocks.Select(block=>block.StartAddress),
            PdfWaveformSampleCatalog.Frames.Select(frame=>frame.StartAddress));

        foreach(var (block,frame) in WaveformCatalog.Blocks.Zip(PdfWaveformSampleCatalog.Frames))
        {
            Assert.Equal(PdfWaveformSampleCatalog.ResponseLength,frame.Response.Length);
            Assert.Equal((byte)0x04,frame.Response.Span[0]);
            Assert.Equal((byte)0x03,frame.Response.Span[1]);
            Assert.Equal((byte)0x80,frame.Response.Span[2]);
            Assert.True(Crc16Modbus.IsValid(frame.Response.Span));
            Assert.Equal(WaveformCatalog.SamplesPerBlock,frame.Registers.Length);

            var values=ReadRegisters(engine,block.StartAddress,block.Count);
            Assert.Equal(frame.Registers.ToArray(),values);
        }

        var gapRequest=Crc16Modbus.Append([1,3,0xB0,0xC0,0,1]);
        var gapResponse=engine.Process(gapRequest)!;
        Assert.True(Crc16Modbus.IsValid(gapResponse));
        Assert.Equal(0x83,gapResponse[1]);
        Assert.Equal(0x02,gapResponse[2]);
    }

    [Fact]
    public async Task Simulator_waveform_service_returns_all_pdf_samples_in_phase_and_time_order()
    {
        var engine=new SimulatorEngine(1);
        await using var client=new SimulatorClient(engine);

        var data=await new WaveformDataService(client).ReadAsync(1);

        Assert.Equal(WaveformCatalog.PointsPerPhase,data.Points.Count);
        foreach(var point in data.Points)
        {
            foreach(var phase in Enum.GetValues<WaveformPhase>())
            {
                var block=WaveformCatalog.GetBlock(point.SegmentIndex,phase);
                var source=PdfWaveformSampleCatalog.Frames.Single(frame=>frame.StartAddress==block.StartAddress);
                var expected=unchecked((short)source.Registers.Span[point.SegmentSampleIndex]);
                var actual=phase switch
                {
                    WaveformPhase.A=>point.PhaseA,
                    WaveformPhase.B=>point.PhaseB,
                    WaveformPhase.C=>point.PhaseC,
                    _=>throw new ArgumentOutOfRangeException(),
                };
                Assert.Equal(expected,actual);
            }
        }

        Assert.Equal(6704,data.Points[128].PhaseA);
        Assert.Equal(-7536,data.Points[191].PhaseA);
        Assert.Equal(1,data.Points[320].PhaseC);
        Assert.Equal(0,data.Points[^1].PhaseC);
        Assert.Equal(2142.413718,data.PhaseARms,6);
        Assert.Equal(1786.377408,data.PhaseBRms,6);
        Assert.Equal(0.835414,data.PhaseCRms,6);
    }

    [Fact]
    public async Task Waveform_excel_contains_analysis_and_address_detail_sheets()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wirelink-waveform-{Guid.NewGuid():N}.xlsx");
        try
        {
            var points=Enumerable.Range(0,WaveformCatalog.PointsPerPhase).Select(index=>
            {
                var segment=index/WaveformCatalog.SamplesPerBlock;
                var local=index%WaveformCatalog.SamplesPerBlock;
                return new WaveformPoint(
                    index,segment,local,WaveformCatalog.GetTimeMilliseconds(index),
                    (short)index,(short)(index+1000),(short)(-index),
                    (ushort)(WaveformCatalog.GetBlock(segment,WaveformPhase.A).StartAddress+local),
                    (ushort)(WaveformCatalog.GetBlock(segment,WaveformPhase.B).StartAddress+local),
                    (ushort)(WaveformCatalog.GetBlock(segment,WaveformPhase.C).StartAddress+local));
            }).ToArray();
            var data=new WaveformData(DateTimeOffset.Now,WaveformCatalog.SampleRateHz,points,1.25,2.5,3.75);

            await new ClosedXmlExportService().ExportAsync(
                path,new WaveformExcelExportContext("录波数据",data));

            using var book=new XLWorkbook(path);
            var analysis=book.Worksheet("波形数据");
            var details=book.Worksheet("读取明细");
            Assert.Equal("采样序号",analysis.Cell(7,1).GetString());
            Assert.Equal(391,analysis.LastRowUsed()!.RowNumber());
            Assert.Equal(-80,analysis.Cell(8,2).GetDouble());
            Assert.Equal(0,analysis.Cell(8,3).GetDouble());
            Assert.Equal(XLDataType.Number,analysis.Cell(8,2).DataType);
            Assert.Equal(1153,details.LastRowUsed()!.RowNumber());
            Assert.Equal("0xB000",details.Cell(2,8).GetString());
            Assert.Equal("0xB5BF",details.Cell(1153,8).GetString());
            Assert.Equal(-383,details.Cell(1153,9).GetDouble());
        }
        finally { if(File.Exists(path))File.Delete(path); }
    }

    [Fact]
    public async Task Waveform_point_details_excel_matches_the_visible_16_column_384_row_table()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wirelink-waveform-points-{Guid.NewGuid():N}.xlsx");
        try
        {
            var points=Enumerable.Range(0,WaveformCatalog.PointsPerPhase).Select(index=>
            {
                var segment=index/WaveformCatalog.SamplesPerBlock;
                var local=index%WaveformCatalog.SamplesPerBlock;
                return new WaveformPoint(
                    index,segment,local,WaveformCatalog.GetTimeMilliseconds(index),
                    checked((short)(index-192)),checked((short)-index),checked((short)(index%3-1)),
                    checked((ushort)(WaveformCatalog.GetBlock(segment,WaveformPhase.A).StartAddress+local)),
                    checked((ushort)(WaveformCatalog.GetBlock(segment,WaveformPhase.B).StartAddress+local)),
                    checked((ushort)(WaveformCatalog.GetBlock(segment,WaveformPhase.C).StartAddress+local)));
            }).ToArray();
            var data=new WaveformData(DateTimeOffset.Now,WaveformCatalog.SampleRateHz,points,1,2,3);

            await new ClosedXmlExportService().ExportAsync(
                path,new WaveformPointDetailsExcelExportContext("录波原始点明细",data));

            using var book=new XLWorkbook(path);
            var sheet=Assert.Single(book.Worksheets);
            Assert.Equal("录波原始点明细",sheet.Name);
            Assert.Equal(385,sheet.LastRowUsed()!.RowNumber());
            Assert.Equal(16,sheet.LastColumnUsed()!.ColumnNumber());
            Assert.Equal("点号",sheet.Cell(1,1).GetString());
            Assert.Equal("C 值(AD)",sheet.Cell(1,16).GetString());

            Assert.Equal(1,sheet.Cell(2,1).GetDouble());
            Assert.Equal("-80～-60 ms",sheet.Cell(2,2).GetString());
            Assert.Equal(1,sheet.Cell(2,3).GetDouble());
            Assert.Equal(-80,sheet.Cell(2,4).GetDouble());
            Assert.Equal("B000H",sheet.Cell(2,5).GetString());
            Assert.Equal("FF40H",sheet.Cell(2,6).GetString());
            Assert.Equal(65344,sheet.Cell(2,7).GetDouble());
            Assert.Equal(-192,sheet.Cell(2,8).GetDouble());
            Assert.Equal("B040H",sheet.Cell(2,9).GetString());
            Assert.Equal("B080H",sheet.Cell(2,13).GetString());
            Assert.Equal("B5BFH",sheet.Cell(385,13).GetString());
            Assert.Equal(XLDataType.Number,sheet.Cell(2,7).DataType);
            Assert.Equal(XLDataType.Number,sheet.Cell(2,8).DataType);
        }
        finally { if(File.Exists(path))File.Delete(path); }
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

    private sealed class SimulatorClient(SimulatorEngine engine) : IModbusRtuClient
    {
        public bool IsOpen=>true;
        public ValueTask OpenAsync(SerialConnectionOptions options,CancellationToken cancellationToken=default)=>
            ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress,ushort startAddress,ushort count,
            CancellationToken cancellationToken=default)=>
            Task.FromResult(ReadRegisters(engine,startAddress,count));
        public Task WriteSingleRegisterAsync(byte slaveAddress,ushort address,ushort value,
            CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
