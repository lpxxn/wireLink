using Avalonia.Controls;
using WireLink.App.ViewModels;
using WireLink.Core.Models;

namespace WireLink.App.Views;

public partial class WaveformRawChartWindow : Window
{
    public WaveformRawChartWindow() => InitializeComponent();

    public WaveformRawChartWindow(WaveformRawChartViewModel viewModel) : this() =>
        DataContext = viewModel;

    public void SetData(WaveformData data) =>
        (DataContext as WaveformRawChartViewModel)?.Load(data);
}
