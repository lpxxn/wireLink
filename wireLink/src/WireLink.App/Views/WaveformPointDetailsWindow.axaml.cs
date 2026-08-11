using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WireLink.App.ViewModels;
using WireLink.Core.Models;
using WireLink.Core.Services;

namespace WireLink.App.Views;

public partial class WaveformPointDetailsWindow : Window
{
    private WaveformRawChartWindow? _rawChartWindow;
    private IExcelExportService? _export;

    public WaveformPointDetailsWindow() => InitializeComponent();

    public WaveformPointDetailsWindow(WaveformPointDetailsViewModel viewModel) : this() =>
        DataContext = viewModel;

    public WaveformPointDetailsWindow(WaveformPointDetailsViewModel viewModel,IExcelExportService? export) : this(viewModel) =>
        _export = export;

    private void OnBodyScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 数据区的水平滚动条是唯一可操作的横向滚动入口。
        // 将相同的 X 偏移应用到隐藏滚动条的表头，使列标题与数据始终保持对齐。
        HeaderScrollViewer.Offset = new Vector(BodyScrollViewer.Offset.X, 0);
    }

    public void SetData(WaveformData? data)
    {
        (DataContext as WaveformPointDetailsViewModel)?.Load(data);
        if (data is not null) _rawChartWindow?.SetData(data);
    }

    private async void OnExportExcelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not WaveformPointDetailsViewModel { CurrentData: { } data } || _export is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出录波 Excel",
            SuggestedFileName = $"录波原始点明细_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            DefaultExtension = "xlsx",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Excel 工作簿") { Patterns = ["*.xlsx"] }],
        });
        if (file is null) return;

        await _export.ExportAsync(
            file.Path.LocalPath,
            new WaveformPointDetailsExcelExportContext("录波原始点明细",data));
    }

    private void OnOpenRawChartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not WaveformPointDetailsViewModel { CurrentData: { } data }) return;
        if (_rawChartWindow is { } existing)
        {
            existing.SetData(data);
            existing.Activate();
            return;
        }

        _rawChartWindow = new WaveformRawChartWindow(new WaveformRawChartViewModel(data));
        _rawChartWindow.Closed += (_, _) => _rawChartWindow = null;
        _rawChartWindow.Show(this);
    }
}
