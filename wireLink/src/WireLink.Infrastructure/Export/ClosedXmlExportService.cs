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
            sheet.Cell(row, 4).Value = $"{context.RecordType} / 第 {context.RecordIndex} 条记录";
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
        var sheet = workbook.Worksheets.Add("波形数据");
        sheet.Cell(1, 1).Value = context.Title;
        sheet.Cell(2, 1).Value = "读取时间";
        sheet.Cell(2, 2).Value = data.ReadAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        sheet.Cell(3, 1).Value = "采样率(Hz)";
        sheet.Cell(3, 2).Value = data.SampleRateHz;
        sheet.Cell(4, 1).Value = "每相点数";
        sheet.Cell(4, 2).Value = data.Points.Count;
        sheet.Cell(5, 1).Value = "A相 AD-RMS";
        sheet.Cell(5, 2).Value = data.PhaseARms;
        sheet.Cell(5, 3).Value = "B相 AD-RMS";
        sheet.Cell(5, 4).Value = data.PhaseBRms;
        sheet.Cell(5, 5).Value = "C相 AD-RMS";
        sheet.Cell(5, 6).Value = data.PhaseCRms;

        var headerRow = 7;
        foreach (var (column, value) in new[]
                 {
                     (1, "采样序号"), (2, "时间(ms)"), (3, "A相AD"),
                     (4, "B相AD"), (5, "C相AD"),
                 })
            sheet.Cell(headerRow, column).Value = value;

        foreach (var (point, index) in data.Points.Select((point, index) => (point, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = headerRow + index + 1;
            sheet.Cell(row, 1).Value = point.SampleIndex;
            sheet.Cell(row, 2).Value = point.TimeMilliseconds;
            sheet.Cell(row, 3).Value = (int)point.PhaseA;
            sheet.Cell(row, 4).Value = (int)point.PhaseB;
            sheet.Cell(row, 5).Value = (int)point.PhaseC;
        }
    }

    private static void WriteWaveformDetailSheet(
        XLWorkbook workbook,
        WaveformExcelExportContext context,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.Worksheets.Add("读取明细");
        foreach (var (column, value) in new[]
                 {
                     (1, "段"), (2, "时间段"), (3, "相别"), (4, "段内序号"),
                     (5, "全局序号"), (6, "时间(ms)"), (7, "地址"),
                     (8, "地址HEX"), (9, "AD值"),
                 })
            sheet.Cell(1, column).Value = value;

        var row = 1;
        foreach (var point in context.Data.Points)
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
                sheet.Cell(row, 7).Value = (int)address;
                sheet.Cell(row, 8).Value = $"0x{address:X4}";
                sheet.Cell(row, 9).Value = (int)value;
            }
        }
    }
}
