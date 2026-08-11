using Avalonia;
using Avalonia.Controls;
using WireLink.App.ViewModels;
using WireLink.Core.Models;

namespace WireLink.App.Views;

public partial class WaveformPointDetailsWindow : Window
{
    private WaveformRawChartWindow? _rawChartWindow;

    public WaveformPointDetailsWindow() => InitializeComponent();

    public WaveformPointDetailsWindow(WaveformPointDetailsViewModel viewModel) : this() =>
        DataContext = viewModel;

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
