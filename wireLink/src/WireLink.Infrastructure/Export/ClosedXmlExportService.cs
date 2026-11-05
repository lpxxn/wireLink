using ClosedXML.Excel;
using WireLink.Core.Models;
using WireLink.Core.Registers;
using WireLink.Core.Services;

namespace WireLink.Infrastructure.Export;

/// <summary>导出与界面一致的名称/计算值双组四列，不写出地址、原始值或隐藏寄存器。</summary>
public sealed class ClosedXmlExportService : IExcelExportService
{
    public Task ExportAsync(string path, ExcelExportContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SafeSheetName(context.Title));
        var row = 1;
        sheet.Cell(row, 1).Value = context.Title;
        row++;
        sheet.Cell(row, 1).Value = "读取时间";
        // 写成明确文本，避免为了日期显示格式而给单元格附加 Excel 样式。
        sheet.Cell(row, 2).Value = context.ReadAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        if (context.RecordType is not null)
        {
            sheet.Cell(row, 3).Value = "记录";
            sheet.Cell(row, 4).Value = $"{DescribeFaultRecordType(context.RecordType.Value)} / 第 {context.RecordIndex} 条记录";
        }
        row += 2;
        foreach (var (column, value) in new[] { (1, "名称"), (2, "计算值"), (3, "名称"), (4, "计算值") })
            sheet.Cell(row, column).Value = value;

        for (var index = 0; index < context.Values.Count; index += 2)
        {
            row++;
            WriteValue(sheet, row, 1, context.Values[index]);
            if (index + 1 < context.Values.Count) WriteValue(sheet, row, 3, context.Values[index + 1]);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        workbook.SaveAs(path);
        return Task.CompletedTask;
    }

    private static string DescribeFaultRecordType(FaultRecordType type) => type switch
    {
        FaultRecordType.Fault => "故障",
        FaultRecordType.Alarm => "报警",
        FaultRecordType.StateChange => "变位",
        _ => type.ToString(),
    };

    /// <summary>导出可直接分析的三相对齐表，以及保留分段、相别和源地址的读取明细表。</summary>
    public Task ExportAsync(
        string path,
        WaveformExcelExportContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var workbook = new XLWorkbook();
        WriteWaveformAnalysisSheet(workbook, context, cancellationToken);
        WriteWaveformDetailSheet(workbook, context, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        workbook.SaveAs(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 导出“录波原始点明细”窗口当前展示的表格：一个三相采样时刻一行，共 16 列、384 行。
    /// </summary>
    public Task ExportAsync(
        string path,
        WaveformPointDetailsExcelExportContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SafeSheetName(context.Title));
        var headers = new[]
        {
            "点号", "时间段", "段内点", "时间(ms)",
            "A 地址", "A 原值(hex)", "A 原值(dec)", "A 值(AD)",
            "B 地址", "B 原值(hex)", "B 原值(dec)", "B 值(AD)",
            "C 地址", "C 原值(hex)", "C 原值(dec)", "C 值(AD)",
        };
        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(1, column + 1).Value = headers[column];

        foreach (var (point, index) in context.Data.Points.Select((point, index) => (point, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = index + 2;
            var segmentStart = -80 + point.SegmentIndex * 20;
            sheet.Cell(row, 1).Value = point.SampleIndex + 1;
            sheet.Cell(row, 2).Value = $"{segmentStart}～{segmentStart + 20} ms";
            sheet.Cell(row, 3).Value = point.SegmentSampleIndex + 1;
            sheet.Cell(row, 4).Value = point.TimeMilliseconds;
            WritePointPhase(sheet, row, 5, point.PhaseAAddress, point.PhaseA);
            WritePointPhase(sheet, row, 9, point.PhaseBAddress, point.PhaseB);
            WritePointPhase(sheet, row, 13, point.PhaseCAddress, point.PhaseC);

            // 与窗口一致：每个 64 点时间段的第 1 点使用浅黄色标出数据块边界。
            if (point.SegmentSampleIndex == 0)
                sheet.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF3CD");
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF3FF");
        sheet.Column(4).Style.NumberFormat.Format = "0.0000";
        sheet.SheetView.FreezeRows(1);
        sheet.RangeUsed()?.SetAutoFilter();
        sheet.Columns().AdjustToContents();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        workbook.SaveAs(path);
        return Task.CompletedTask;
    }

    private static void WritePointPhase(IXLWorksheet sheet, int row, int startColumn, ushort address, short value)
    {
        var raw = unchecked((ushort)value);
        sheet.Cell(row, startColumn).Value = $"{address:X4}H";
        sheet.Cell(row, startColumn + 1).Value = $"{raw:X4}H";
        sheet.Cell(row, startColumn + 2).Value = (int)raw;
        sheet.Cell(row, startColumn + 3).Value = (int)value;
    }

    private static void WriteValue(IXLWorksheet sheet, int row, int column, DecodedValue value)
    {
        sheet.Cell(row, column).Value = value.Name;
        sheet.Cell(row, column + 1).Value = value.DisplayValue;
    }

    private static string SafeSheetName(string value)
    {
        foreach (var invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' }) value = value.Replace(invalid, '_');
        return value.Length > 31 ? value[..31] : value;
    }

    private static void WriteWaveformAnalysisSheet(
        XLWorkbook workbook,
        WaveformExcelExportContext context,
        CancellationToken cancellationToken)
    {
        var data = context.Data;
        var timing = data.Timing ?? throw new InvalidOperationException("录波数据没有故障记录时间，不能导出绝对时间。");
        if (data.Segments.Count != WaveformCatalog.SegmentCount || data.Points.Count != WaveformCatalog.PointsPerPhase)
            throw new InvalidOperationException("仅允许导出6个完整时间段的录波数据。");
        var sheet = workbook.Worksheets.Add("波形数据");
        sheet.Cell(1, 1).Value = context.Title;
        sheet.Cell(2, 1).Value = "读取时间";
        sheet.Cell(2, 2).Value = data.ReadAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        sheet.Cell(3, 1).Value = "采样率(Hz)";
        sheet.Cell(3, 2).Value = data.SampleRateHz;
        // sheet.Cell(3, 4).Value = "1552原值";
        // sheet.Cell(3, 5).Value = (int)data.Calibration.RegisterValue;
        sheet.Cell(3, 4).Value = "框架";
        sheet.Cell(3, 5).Value = data.Calibration.FrameName;
        sheet.Cell(4, 1).Value = "每相点数";
        sheet.Cell(4, 2).Value = data.Points.Count;
        sheet.Cell(4, 4).Value = "Rate";
        sheet.Cell(4, 5).Value = data.Calibration.Rate;
        // sheet.Cell(4, 7).Value = "记录";
        // sheet.Cell(4, 8).Value = $"{DescribeFaultRecordType(timing.RecordType)} / 第 {timing.RecordIndex} 条";
        // sheet.Cell(5, 1).Value = "故障时间";
        // sheet.Cell(5, 2).Value = timing.FaultRecordTime;
        // sheet.Cell(5, 2).Style.NumberFormat.Format = "yyyy-mm-dd hh:mm:ss";
        // sheet.Cell(6, 1).Value = "软件补充毫秒";
        // sheet.Cell(6, 2).Value = timing.SoftwareMilliseconds;
        // sheet.Cell(6, 4).Value = "非设备实测毫秒";
        // sheet.Cell(6, 1).Value = "录波首点";
        // sheet.Cell(6, 2).Value = timing.WaveformStartTime;
        // sheet.Cell(6, 2).Style.NumberFormat.Format = "yyyy-mm-dd hh:mm:ss.000";
        // sheet.Cell(6, 4).Value = "录波末点";
        // sheet.Cell(6, 5).Value = data.WaveformEndTime;
        // sheet.Cell(6, 5).Style.NumberFormat.Format = "yyyy-mm-dd hh:mm:ss.000";
        // sheet.Cell(8, 1).Value = "分块RMS公式";
        // sheet.Cell(8, 2).Value = "sqrt(sum(有符号AD²)/64) × 10000.0 ÷ 22953.0 × Rate";
        // sheet.Cell(9, 1).Value = "单位说明";
        // sheet.Cell(9, 2).Value = "采样行保留有符号AD；每64行后的RMS行单位为A，保留1位小数";

        const int headerRow = 6;
        var headers = new[] { "类型", "时间段", "点号", "相对时间(ms)", "绝对采样时间", "单位", "A相", "B相", "C相" };
        for (var column = 0; column < headers.Length; column++)
            sheet.Cell(headerRow, column + 1).Value = headers[column];
        var row = headerRow;
        foreach (var segment in data.Segments)
        {
            foreach (var point in segment.Points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                row++;
                sheet.Cell(row, 1).Value = "采样";
                sheet.Cell(row, 2).Value = segment.TimeRangeText;
                sheet.Cell(row, 3).Value = point.SampleIndex + 1;
                sheet.Cell(row, 4).Value = point.TimeMilliseconds;
                sheet.Cell(row, 4).Style.NumberFormat.Format = "0.0000";
                WriteWaveformTime(sheet.Cell(row, 5), data.GetAbsoluteTime(point.TimeMilliseconds));
                sheet.Cell(row, 6).Value = "AD";
                sheet.Cell(row, 7).Value = (int)point.PhaseA;
                sheet.Cell(row, 8).Value = (int)point.PhaseB;
                sheet.Cell(row, 9).Value = (int)point.PhaseC;
                sheet.Range(row, 7, row, 9).Style.NumberFormat.Format = "0";
            }
            row++;
            sheet.Cell(row, 1).Value = "RMS";
            sheet.Cell(row, 2).Value = segment.TimeRangeText;
            sheet.Cell(row, 6).Value = "A";
            sheet.Cell(row, 7).Value = segment.PhaseA.AmperesRms;
            sheet.Cell(row, 8).Value = segment.PhaseB.AmperesRms;
            sheet.Cell(row, 9).Value = segment.PhaseC.AmperesRms;
            sheet.Range(row, 7, row, 9).Style.NumberFormat.Format = "0.0";
            var summary = sheet.Range(row, 1, row, 9);
            summary.Style.Font.Bold = true;
            summary.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF3FF");
            summary.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }
        sheet.Row(headerRow).Style.Font.Bold = true;
        sheet.Row(headerRow).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF3FF");
        sheet.SheetView.FreezeRows(headerRow);
        sheet.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.Column(1).Width = 18;
        sheet.Column(2).Width = 28;
        sheet.Column(3).Width = 10;
        sheet.Column(4).Width = 18;
        sheet.Column(5).Width = 27;
        sheet.Column(6).Width = 10;
        sheet.Columns(7, 9).Width = 16;
    }

    private static void WriteWaveformDetailSheet(
        XLWorkbook workbook,
        WaveformExcelExportContext context,
        CancellationToken cancellationToken)
    {
        var data = context.Data;
        var sheet = workbook.Worksheets.Add("读取明细");
        sheet.Cell(1, 1).Value = "录波读取明细";

        const int headerRow = 3;
        foreach (var (column, value) in new[]
                 {
                     (1, "段"), (2, "时间段"), (3, "相别"), (4, "段内序号"),
                     (5, "全局序号"), (6, "相对时间(ms)"), (7, "绝对采样时间"),
                     (8, "地址"), (9, "地址HEX"), (10, "AD值"),
                 })
            sheet.Cell(headerRow, column).Value = value;

        var row = headerRow;
        foreach (var point in data.Points)
        {
            foreach (var phase in Enum.GetValues<WaveformPhase>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                row++;
                var block = WaveformCatalog.GetBlock(point.SegmentIndex, phase);
                var (address, value) = phase switch
                {
                    WaveformPhase.A => (point.PhaseAAddress, point.PhaseA),
                    WaveformPhase.B => (point.PhaseBAddress, point.PhaseB),
                    WaveformPhase.C => (point.PhaseCAddress, point.PhaseC),
                    _ => throw new ArgumentOutOfRangeException(nameof(phase)),
                };

                sheet.Cell(row, 1).Value = point.SegmentIndex + 1;
                sheet.Cell(row, 2).Value = block.TimeRangeText;
                sheet.Cell(row, 3).Value = $"{phase}相";
                sheet.Cell(row, 4).Value = point.SegmentSampleIndex;
                sheet.Cell(row, 5).Value = point.SampleIndex;
                sheet.Cell(row, 6).Value = point.TimeMilliseconds;
                var absoluteTimeCell = sheet.Cell(row, 7);
                WriteWaveformTime(absoluteTimeCell, data.GetAbsoluteTime(point.TimeMilliseconds));
                sheet.Cell(row, 8).Value = (int)address;
                sheet.Cell(row, 9).Value = $"0x{address:X4}";
                sheet.Cell(row, 10).Value = (int)value;
            }
        }
        sheet.Column(6).Style.NumberFormat.Format = "0.0000";
        sheet.Column(7).Style.NumberFormat.Format = "yyyy-mm-dd hh:mm:ss.000";

        sheet.Row(headerRow).Style.Font.Bold = true;
        sheet.Row(headerRow).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF3FF");
        sheet.SheetView.FreezeRows(headerRow);
        sheet.Range(headerRow, 1, row, 10).SetAutoFilter();
        sheet.Column(1).Width = 9;
        sheet.Column(2).Width = 18;
        sheet.Column(3).Width = 9;
        sheet.Column(7).Width = 27;
        sheet.Columns(8, 10).Width = 16;
    }

    private static void WriteWaveformTime(IXLCell cell, DateTime time)
    {
        // DateTime.ToOADate会截断不足1ms的ticks。直接写Excel日期序列，保留0.3125ms间隔。
        cell.Value = (time.Ticks - new DateTime(1899, 12, 30).Ticks) / (double)TimeSpan.TicksPerDay;
        cell.Style.NumberFormat.Format = "yyyy-mm-dd hh:mm:ss.000";
    }
}
