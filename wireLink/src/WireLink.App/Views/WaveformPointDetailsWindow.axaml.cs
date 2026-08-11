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
