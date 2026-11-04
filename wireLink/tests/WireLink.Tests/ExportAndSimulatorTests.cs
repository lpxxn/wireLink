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
    static ExportAndSimulatorTests()
    {
        // 使用ClosedXML自带的度量字体，测试不依赖系统字体目录访问权限。
        using var font = typeof(XLWorkbook).Assembly.GetManifestResourceStream("ClosedXML.Graphics.Fonts.CarlitoBare-Regular.ttf")!;
        LoadOptions.DefaultGraphicEngine = ClosedXML.Graphics.DefaultGraphicEngine.CreateOnlyWithFonts(font);
    }

    [Fact]
    public async Task Excel_uses_four_columns_without_addresses_or_raw_values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wirelink-{Guid.NewGuid():N}.xlsx");
        try
        {
            var values = Enumerable.Range(0, 3).Select(i => new DecodedValue($"字段{i}", [(ushort)(256 + i)], $"值{i}", "V", "公式",
                [new RawRegisterSample((ushort)(256 + i), 0xABCD, DateTimeOffset.Now)],
                i == 1 ? ParseStatus.InvalidData : ParseStatus.Success,
                i == 1 ? "测试警告" : null,
                DateTimeOffset.Now)).ToArray();
            await new ClosedXmlExportService().ExportAsync(path, new ExcelExportContext("设备数据", values, DateTimeOffset.Now));
            using var book = new XLWorkbook(path); var sheet = book.Worksheet(1);
            Assert.Equal("名称", sheet.Cell(4, 1).GetString()); Assert.Equal("计算值", sheet.Cell(4, 4).GetString());
            var text = string.Join('|', sheet.CellsUsed().Select(c => c.GetString()));
            Assert.DoesNotContain("256", text); Assert.DoesNotContain("ABCD", text);
            Assert.Empty(sheet.MergedRanges);
            Assert.All(sheet.CellsUsed(), cell =>
            {
                Assert.False(cell.Style.Font.Bold);
                Assert.Equal(XLFillPatternValues.None, cell.Style.Fill.PatternType);
                Assert.Equal(XLBorderStyleValues.None, cell.Style.Border.TopBorder);
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(FaultRecordType.Fault, "故障 / 第 1 条记录")]
    [InlineData(FaultRecordType.Alarm, "报警 / 第 1 条记录")]
    [InlineData(FaultRecordType.StateChange, "变位 / 第 1 条记录")]
    public async Task Fault_excel_uses_chinese_record_type(FaultRecordType recordType, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wirelink-{Guid.NewGuid():N}.xlsx");
        try
        {
            await new ClosedXmlExportService().ExportAsync(
                path,
                new ExcelExportContext("故障数据", [], DateTimeOffset.Now, recordType, 1));

            using var book = new XLWorkbook(path);
            Assert.Equal(expected, book.Worksheet(1).Cell(2, 4).GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Simulator_supports_03_and_06_with_crc()
    {
        var engine = new SimulatorEngine(1);
        var read = Crc16Modbus.Append([1, 3, 1, 0, 0, 1]);
        var response = engine.Process(read)!;
        Assert.True(Crc16Modbus.IsValid(response)); Assert.Equal(230, (response[3] << 8) | response[4]);
        var thermalResponse = engine.Process(Crc16Modbus.Append([1, 3, 1, 23, 0, 1]))!;
        Assert.Equal(68, (thermalResponse[3] << 8) | thermalResponse[4]);
        var operationResponse = engine.Process(Crc16Modbus.Append([1, 3, 4, 7, 0, 1]))!;
        Assert.Equal(128, (operationResponse[3] << 8) | operationResponse[4]);
        var ratedCurrentConfigurationResponse = engine.Process(Crc16Modbus.Append([1, 3, 6, 16, 0, 1]))!;
        Assert.Equal(0x0104, (ratedCurrentConfigurationResponse[3] << 8) | ratedCurrentConfigurationResponse[4]);
        var write = Crc16Modbus.Append([1, 6, 3, 17, 2, 1]);
        var echo = engine.Process(write)!;
        Assert.Equal(write, echo);
    }

    [Fact]
    public void Simulator_exposes_molded_case_001d_raw_value()
    {
        var engine = new SimulatorEngine(1);

        Assert.Equal((ushort)35, ReadRegisters(engine, 0x001D, 1).Single());
    }

    [Fact]
    public async Task Simulator_exposes_frame_controller_protection_data()
    {
        var engine = new SimulatorEngine(1);
        await using var client = new SimulatorClient(engine);

        var result = await new ProtectionDataService(client, new RegisterParser()).ReadAsync(
            1, DeviceType.FrameController, WordOrder.HighWordFirst, BreakerSeries.BW1);

        Assert.Empty(result.Errors);
        Assert.Equal("地电流型", result.Values.Single(value => value.Name == "接地保护方式").DisplayValue);
        Assert.Equal("630 A", result.Values.Single(value => value.Name == "过载动作值").DisplayValue);
        Assert.Equal("315 A", result.Values.Single(value => value.Name == "保护动作值").DisplayValue);
        Assert.Equal("0.25 s", result.Values.Single(value => value.Name == "保护动作时间").DisplayValue);
        Assert.Equal("30%", result.Values.Single(value => value.Name == "I 不平衡启动值").DisplayValue);
        Assert.Equal("20%", result.Values.Single(value => value.Name == "I 不平衡返回值").DisplayValue);
    }

    [Theory]
    [InlineData(0, "关闭", "315", ParseStatus.ProtocolUnconfirmed)]
    [InlineData(1, "漏电型", "3.15 A", ParseStatus.Success)]
    [InlineData(2, "差值型", "315", ParseStatus.ProtocolUnconfirmed)]
    [InlineData(3, "地电流型", "315 A", ParseStatus.Success)]
    public async Task Simulator_can_change_1793_ground_protection_mode(
        byte mode, string expectedMode, string expectedActionValue, ParseStatus expectedActionStatus)
    {
        var engine = new SimulatorEngine(1);
        engine.SetGroundProtectionMode(mode);
        await using var client = new SimulatorClient(engine);

        Assert.Equal(mode, engine.GroundProtectionMode);
        var register = ReadRegisters(engine, RegisterCatalog.GroundProtectionModeRegisterAddress, 1).Single();
        Assert.Equal(mode, (byte)((register & RegisterCatalog.GroundProtectionModeMask) >> 10));

        var result = await new ProtectionDataService(client, new RegisterParser()).ReadAsync(
            1, DeviceType.FrameController, WordOrder.HighWordFirst, BreakerSeries.BW1);
        var decoded = result.Values.Single(value => value.Name == "接地保护方式");
        Assert.Equal(expectedMode, decoded.DisplayValue);
        Assert.Equal(mode == 2 ? ParseStatus.ProtocolUnconfirmed : ParseStatus.Success, decoded.Status);
        var actionValue = result.Values.Single(value => value.Name == "保护动作值");
        Assert.Equal(expectedActionValue, actionValue.DisplayValue);
        Assert.Equal(expectedActionStatus, actionValue.Status);
    }

    [Fact]
    public void Simulator_rejects_unsupported_ground_protection_mode()
    {
        var engine = new SimulatorEngine(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetGroundProtectionMode(4));
        Assert.Equal((byte)3, engine.GroundProtectionMode);
    }

    [Fact]
    public void Simulator_can_inject_bad_crc_once()
    {
        var engine = new SimulatorEngine(1) { FaultMode = SimulatorFaultMode.BadCrcOnce };
        var request = Crc16Modbus.Append([1, 3, 1, 0, 0, 1]);
        Assert.False(Crc16Modbus.IsValid(engine.Process(request)!));
        Assert.True(Crc16Modbus.IsValid(engine.Process(request)!));
    }

    [Fact]
    public void Simulator_exposes_all_18_waveform_blocks_but_not_address_gaps()
    {
        var engine = new SimulatorEngine(1, SimulatorWaveformSource.Pdf);
        Assert.Equal(WaveformCatalog.TotalBlocks, PdfWaveformSampleCatalog.Frames.Count);
        Assert.Equal(
            WaveformCatalog.Blocks.Select(block => block.StartAddress),
            PdfWaveformSampleCatalog.Frames.Select(frame => frame.StartAddress));

        foreach (var (block, frame) in WaveformCatalog.Blocks.Zip(PdfWaveformSampleCatalog.Frames))
        {
            Assert.Equal(PdfWaveformSampleCatalog.ResponseLength, frame.Response.Length);
            Assert.Equal((byte)0x04, frame.Response.Span[0]);
            Assert.Equal((byte)0x03, frame.Response.Span[1]);
            Assert.Equal((byte)0x80, frame.Response.Span[2]);
            Assert.True(Crc16Modbus.IsValid(frame.Response.Span));
            Assert.Equal(WaveformCatalog.SamplesPerBlock, frame.Registers.Length);

            var values = ReadRegisters(engine, block.StartAddress, block.Count);
            Assert.Equal(frame.Registers.ToArray(), values);
        }

        var gapRequest = Crc16Modbus.Append([1, 3, 0xB0, 0xC0, 0, 1]);
        var gapResponse = engine.Process(gapRequest)!;
        Assert.True(Crc16Modbus.IsValid(gapResponse));
        Assert.Equal(0x83, gapResponse[1]);
        Assert.Equal(0x02, gapResponse[2]);
    }

    [Fact]
    public async Task Simulator_waveform_service_returns_all_pdf_samples_in_phase_and_time_order()
    {
        var engine = new SimulatorEngine(1, SimulatorWaveformSource.Pdf);
        await using var client = new SimulatorClient(engine);

        var data = await new WaveformDataService(client).ReadAsync(1);

        Assert.Equal(WaveformCatalog.PointsPerPhase, data.Points.Count);
        foreach (var point in data.Points)
        {
            foreach (var phase in Enum.GetValues<WaveformPhase>())
            {
                var block = WaveformCatalog.GetBlock(point.SegmentIndex, phase);
                var source = PdfWaveformSampleCatalog.Frames.Single(frame => frame.StartAddress == block.StartAddress);
                var expected = unchecked((short)source.Registers.Span[point.SegmentSampleIndex]);
                var actual = phase switch
                {
                    WaveformPhase.A => point.PhaseA,
                    WaveformPhase.B => point.PhaseB,
                    WaveformPhase.C => point.PhaseC,
                    _ => throw new ArgumentOutOfRangeException(),
                };
                Assert.Equal(expected, actual);
            }
        }

        Assert.Equal(6704, data.Points[128].PhaseA);
        Assert.Equal(-7536, data.Points[191].PhaseA);
        Assert.Equal(1, data.Points[320].PhaseC);
        Assert.Equal(0, data.Points[^1].PhaseC);
        Assert.Equal(6, data.Segments.Count);
        Assert.Equal(4978.873768233133, data.Segments[2].PhaseA.AdRms, 10);
        Assert.Equal(0.7905694150420949, data.Segments[5].PhaseC.AdRms, 12);
        Assert.Equal((ushort)0x0104, data.Calibration.RegisterValue);
        Assert.Equal(WaveformCalibration.RoundAmperes(4978.873768233133 * 15000.0 / 22953.0),
            data.Segments[2].PhaseA.AmperesRms);
    }

    [Fact]
    public void Recorded_waveform_is_default_and_preserves_all_1152_original_registers()
    {
        var engine = new SimulatorEngine(1);
        Assert.Equal(SimulatorWaveformSource.Recorded20261104, engine.WaveformSource);
        Assert.Equal("录波原始点明细_20261104_084756.xlsx", engine.WaveformSourceName);
        Assert.Equal(18, RecordedWaveformSampleCatalog.Blocks.Count);
        var bytes = new List<byte>();
        foreach (var (block, definition) in RecordedWaveformSampleCatalog.Blocks.Zip(WaveformCatalog.Blocks))
        {
            Assert.Equal(definition.StartAddress, block.StartAddress);
            Assert.Equal(64, block.Registers.Count);
            var actual = ReadRegisters(engine, block.StartAddress, 64);
            Assert.Equal(block.Registers, actual);
            foreach (var value in actual)
            {
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
            }
        }
        Assert.Equal("ed2e6e7c7dacecadddd42fbf84f380325d690733e6eddd15537c4749922acc43",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        Assert.Equal((ushort)0, ReadRegisters(engine, 0xB000, 1)[0]);
        Assert.Equal((ushort)1, ReadRegisters(engine, 0xB040, 1)[0]);
        Assert.Equal((ushort)433, ReadRegisters(engine, 0xB080, 1)[0]);
        Assert.Equal((ushort)0xFFFA, ReadRegisters(engine, 0xB5BF, 1)[0]);
        var gap = engine.Process(Crc16Modbus.Append([1, 3, 0xB0, 0xC0, 0, 1]))!;
        Assert.Equal((byte)0x83, gap[1]);
        Assert.Equal((byte)2, gap[2]);
    }

    [Fact]
    public async Task Recorded_waveform_builds_six_segments_with_workbook_rms_and_signed_values()
    {
        await using var client = new SimulatorClient(new SimulatorEngine(1));
        var data = await new WaveformDataService(client).ReadAsync(1);
        double[][] expected =
        [
            [0.7905694150420949, 0.9842509842514764, 616.0819116602921],
            [0.81967981553775, 1.1110243021644486, 616.4701178889371],
            [0.82915619758885, 0.9437293044088437, 616.650909145523],
            [0.8385254915624212, 1.0231690964840563, 617.0511906438558],
            [0.9762812094883317, 1.159202311936963, 505.37506183526705],
            [0.5590169943749475, 0.673145600891813, 3.5333235062756425],
        ];
        Assert.Equal((ushort)0x0104, data.Calibration.RegisterValue);
        Assert.Equal("框II", data.Calibration.FrameName);
        Assert.Equal(1.5, data.Calibration.Rate);
        Assert.Equal(384, data.Points.Count);
        Assert.Equal(6, data.Segments.Count);
        Assert.Equal(433, data.Points[0].PhaseC);
        Assert.Equal(-6, data.Points[^1].PhaseC);
        Assert.Equal(-80, data.Points[0].TimeMilliseconds);
        Assert.Equal(39.6875, data.Points[^1].TimeMilliseconds);
        for (var index = 0; index < 6; index++)
        {
            var segment = data.Segments[index];
            var blocks = new[] { segment.PhaseA, segment.PhaseB, segment.PhaseC };
            for (var phase = 0; phase < 3; phase++)
            {
                Assert.Equal(expected[index][phase], blocks[phase].AdRms, 10);
                Assert.Equal(WaveformCalibration.RoundAmperes(expected[index][phase] * 15000.0 / 22953.0),
                    blocks[phase].AmperesRms);
            }
        }
    }

    [Fact]
    public async Task Waveform_excel_contains_analysis_and_address_detail_sheets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wirelink-waveform-{Guid.NewGuid():N}.xlsx");
        try
        {
            var points = Enumerable.Range(0, WaveformCatalog.PointsPerPhase).Select(index =>
            {
                var segment = index / WaveformCatalog.SamplesPerBlock;
                var local = index % WaveformCatalog.SamplesPerBlock;
                return new WaveformPoint(
                    index, segment, local, WaveformCatalog.GetTimeMilliseconds(index),
                    (short)index, (short)(index + 1000), (short)(-index),
                    (ushort)(WaveformCatalog.GetBlock(segment, WaveformPhase.A).StartAddress + local),
                    (ushort)(WaveformCatalog.GetBlock(segment, WaveformPhase.B).StartAddress + local),
                    (ushort)(WaveformCatalog.GetBlock(segment, WaveformPhase.C).StartAddress + local));
            }).ToArray();
            var calibration = WaveformCalibration.FromRegisterValue(0x0204);
            var timing = new WaveformTiming(
                new DateTime(2026, 7, 22, 14, 30, 1, DateTimeKind.Unspecified),
                347,
                FaultRecordType.Fault,
                1);
            var data = WaveformTestData.Create(DateTimeOffset.Now, WaveformCatalog.SampleRateHz, points, calibration) with
            {
                Timing = timing,
            };

            await new ClosedXmlExportService().ExportAsync(
                path, new WaveformExcelExportContext("录波数据", data));

            using var book = new XLWorkbook(path);
            var analysis = book.Worksheet("波形数据");
            var details = book.Worksheet("读取明细");
            Assert.Equal("类型", analysis.Cell(11, 1).GetString());
            Assert.Equal(401, analysis.LastRowUsed()!.RowNumber());
            Assert.Equal(516, analysis.Cell(3, 5).GetDouble());
            Assert.Equal("框III", analysis.Cell(3, 8).GetString());
            Assert.Equal(2, analysis.Cell(4, 5).GetDouble());
            Assert.Contains("22953.0", analysis.Cell(8, 2).GetString());
            Assert.Equal(347, analysis.Cell(6, 2).GetDouble());
            Assert.Contains("非设备实测", analysis.Cell(6, 4).GetString());
            Assert.False(analysis.AutoFilter.IsEnabled);
            for (var segment = 0; segment < 6; segment++)
            {
                var firstRow = 12 + segment * 65;
                var rmsRow = firstRow + 64;
                for (var offset = 0; offset < 64; offset++)
                {
                    var row = firstRow + offset;
                    var index = segment * 64 + offset;
                    Assert.Equal("采样", analysis.Cell(row, 1).GetString());
                    Assert.Equal("AD", analysis.Cell(row, 6).GetString());
                    Assert.Equal(index + 1, analysis.Cell(row, 3).GetDouble());
                    Assert.Equal(index, analysis.Cell(row, 7).GetDouble());
                    Assert.Equal(index + 1000, analysis.Cell(row, 8).GetDouble());
                    Assert.Equal(-index, analysis.Cell(row, 9).GetDouble());
                    Assert.Equal(XLDataType.Number, analysis.Cell(row, 7).DataType);
                    Assert.Equal(XLDataType.DateTime, analysis.Cell(row, 5).DataType);
                    Assert.InRange(Math.Abs((data.GetAbsoluteTime(points[index].TimeMilliseconds) - analysis.Cell(row, 5).GetDateTime()).TotalMilliseconds), 0, 1);
                }
                Assert.Equal("RMS", analysis.Cell(rmsRow, 1).GetString());
                Assert.Equal("A", analysis.Cell(rmsRow, 6).GetString());
                Assert.True(analysis.Cell(rmsRow, 3).IsEmpty());
                Assert.True(analysis.Cell(rmsRow, 1).Style.Font.Bold);
                Assert.Equal(data.Segments[segment].PhaseA.AmperesRms, analysis.Cell(rmsRow, 7).GetDouble());
                Assert.Equal(data.Segments[segment].PhaseB.AmperesRms, analysis.Cell(rmsRow, 8).GetDouble());
                Assert.Equal(data.Segments[segment].PhaseC.AmperesRms, analysis.Cell(rmsRow, 9).GetDouble());
                Assert.Equal("0.0", analysis.Cell(rmsRow, 7).Style.NumberFormat.Format);
            }
            Assert.Equal(1155, details.LastRowUsed()!.RowNumber());
            Assert.Equal(10, details.LastColumnUsed()!.ColumnNumber());
            Assert.True(details.AutoFilter.IsEnabled);
            Assert.Equal("绝对采样时间", details.Cell(3, 7).GetString());
            Assert.Equal(timing.WaveformStartTime, details.Cell(4, 7).GetDateTime());
            Assert.Equal("0xB000", details.Cell(4, 9).GetString());
            Assert.Equal("0xB5BF", details.Cell(1155, 9).GetString());
            Assert.Equal(-383, details.Cell(1155, 10).GetDouble());
            Assert.Equal(XLDataType.Number, details.Cell(1155, 10).DataType);
            Assert.Equal("yyyy-mm-dd hh:mm:ss.000", details.Cell(1155, 7).Style.NumberFormat.Format);
            // ClosedXML读取日期时仅返回毫秒精度；检查xlsx底层序列以验证0.3125ms未丢失。
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            using var sheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = System.Xml.Linq.XDocument.Load(sheetStream);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            double ReadSerial(string address) => double.Parse(xml.Descendants(ns + "c")
                .Single(cell => (string?)cell.Attribute("r") == address).Element(ns + "v")!.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            // Excel序列采用15位有效数字，允许0.01ms序列量化误差，不能退化为重复的毫秒时间。
            Assert.InRange(Math.Abs((ReadSerial("E13") - ReadSerial("E12")) * 86400000 - 0.3125), 0, 0.01);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Waveform_point_details_excel_matches_the_visible_16_column_384_row_table()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wirelink-waveform-points-{Guid.NewGuid():N}.xlsx");
        try
        {
            var points = Enumerable.Range(0, WaveformCatalog.PointsPerPhase).Select(index =>
            {
                var segment = index / WaveformCatalog.SamplesPerBlock;
                var local = index % WaveformCatalog.SamplesPerBlock;
                return new WaveformPoint(
                    index, segment, local, WaveformCatalog.GetTimeMilliseconds(index),
                    checked((short)(index - 192)), checked((short)-index), checked((short)(index % 3 - 1)),
                    checked((ushort)(WaveformCatalog.GetBlock(segment, WaveformPhase.A).StartAddress + local)),
                    checked((ushort)(WaveformCatalog.GetBlock(segment, WaveformPhase.B).StartAddress + local)),
                    checked((ushort)(WaveformCatalog.GetBlock(segment, WaveformPhase.C).StartAddress + local)));
            }).ToArray();
            var data = WaveformTestData.Create(DateTimeOffset.Now, WaveformCatalog.SampleRateHz, points, WaveformCalibration.FromRegisterValue(0x0204));

            await new ClosedXmlExportService().ExportAsync(
                path, new WaveformPointDetailsExcelExportContext("录波原始点明细", data));

            using var book = new XLWorkbook(path);
            var sheet = Assert.Single(book.Worksheets);
            Assert.Equal("录波原始点明细", sheet.Name);
            Assert.Equal(385, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal(16, sheet.LastColumnUsed()!.ColumnNumber());
            Assert.Equal("点号", sheet.Cell(1, 1).GetString());
            Assert.Equal("C 值(AD)", sheet.Cell(1, 16).GetString());

            Assert.Equal(1, sheet.Cell(2, 1).GetDouble());
            Assert.Equal("-80～-60 ms", sheet.Cell(2, 2).GetString());
            Assert.Equal(1, sheet.Cell(2, 3).GetDouble());
            Assert.Equal(-80, sheet.Cell(2, 4).GetDouble());
            Assert.Equal("B000H", sheet.Cell(2, 5).GetString());
            Assert.Equal("FF40H", sheet.Cell(2, 6).GetString());
            Assert.Equal(65344, sheet.Cell(2, 7).GetDouble());
            Assert.Equal(-192, sheet.Cell(2, 8).GetDouble());
            Assert.Equal("B040H", sheet.Cell(2, 9).GetString());
            Assert.Equal("B080H", sheet.Cell(2, 13).GetString());
            Assert.Equal("B5BFH", sheet.Cell(385, 13).GetString());
            Assert.Equal(XLDataType.Number, sheet.Cell(2, 7).DataType);
            Assert.Equal(XLDataType.Number, sheet.Cell(2, 8).DataType);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Simulator_can_switch_current_fault_and_alarm_registers()
    {
        var engine = new SimulatorEngine(1);

        engine.SetCurrentEvent(SimulatorCurrentEventMode.Fault);
        var fault = ReadRegisters(engine, 512, 12);
        Assert.Equal(0x0002 | (1 << 3) | (1 << 10), fault[0]);
        Assert.Equal(0x0700, fault[3]);
        Assert.Equal(125, fault[4]);

        engine.SetCurrentEvent(SimulatorCurrentEventMode.Alarm);
        var alarm = ReadRegisters(engine, 512, 12);
        Assert.Equal(0x0002 | (1 << 2) | (1 << 11), alarm[0]);
        Assert.Equal(1 << 2, alarm[1]);
        Assert.Equal(0x0300, alarm[3]);
        Assert.Equal(125, alarm[4]);

        engine.SetCurrentEvent(SimulatorCurrentEventMode.Normal);
        var normal = ReadRegisters(engine, 512, 12);
        Assert.Equal(0x0002, normal[0]);
        Assert.All(normal.Skip(1), value => Assert.Equal(0, value));
    }

    private static ushort[] ReadRegisters(SimulatorEngine engine, ushort start, ushort count)
    {
        var request = Crc16Modbus.Append([
            (byte)1, (byte)3,
            (byte)(start >> 8), (byte)start,
            (byte)(count >> 8), (byte)count]);
        var response = engine.Process(request)!;
        Assert.True(Crc16Modbus.IsValid(response));
        return Enumerable.Range(0, count)
            .Select(i => (ushort)((response[3 + i * 2] << 8) | response[4 + i * 2]))
            .ToArray();
    }

    private sealed class SimulatorClient(SimulatorEngine engine) : IModbusRtuClient
    {
        public bool IsOpen => true;
        public ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort count,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadRegisters(engine, startAddress, count));
        public Task WriteSingleRegisterAsync(byte slaveAddress, ushort address, ushort value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
