using ClosedXML.Excel;
using WireLink.Core.Models;
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
            sheet.Cell(row, 4).Value = $"{context.RecordType} / 记录 {context.RecordIndex}";
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
}
