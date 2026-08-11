using WireLink.App.ViewModels;
using WireLink.Core.Models;

namespace WireLink.Tests;

public sealed class WaveformRawChartViewModelTests
{
    [Fact]
    public void Raw_chart_uses_uint16_values_without_signed_conversion()
    {
        var points = new[]
        {
            Point(0, -80, -1, -320, 1),
            Point(1, -79.6875, short.MinValue, short.MaxValue, 0),
        };
        var data = new WaveformData(DateTimeOffset.Now, 3200, points, 0, 0, 0);

        var viewModel = new WaveformRawChartViewModel(data);

        Assert.Equal(2, viewModel.Points.Count);
        Assert.Equal(65535, viewModel.Points[0].PhaseA);
        Assert.Equal(65216, viewModel.Points[0].PhaseB);
        Assert.Equal(1, viewModel.Points[0].PhaseC);
        Assert.Equal(32768, viewModel.Points[1].PhaseA);
        Assert.Equal(32767, viewModel.Points[1].PhaseB);
        Assert.Equal(0, viewModel.Points[1].PhaseC);
        Assert.Equal(0, viewModel.YAxes[0].MinLimit);
        Assert.Equal(ushort.MaxValue, viewModel.YAxes[0].MaxLimit);
        Assert.Equal(3, viewModel.Series.Count);
    }

    [Fact]
    public void Raw_chart_phase_switches_only_change_visibility_not_points()
    {
        var data = new WaveformData(
            DateTimeOffset.Now,
            3200,
            [Point(0, -80, -1, 2, -3)],
            0,
            0,
            0);
        var viewModel = new WaveformRawChartViewModel(data);

        viewModel.ShowPhaseA = false;
        viewModel.ShowPhaseC = false;

        Assert.Single(viewModel.Series);
        Assert.Single(viewModel.Points);
        Assert.Equal(65535, viewModel.Points[0].PhaseA);
        Assert.Equal(2, viewModel.Points[0].PhaseB);
        Assert.Equal(65533, viewModel.Points[0].PhaseC);
    }

    private static WaveformPoint Point(
        int sampleIndex,
        double time,
        short phaseA,
        short phaseB,
        short phaseC) =>
        new(sampleIndex, 0, sampleIndex, time, phaseA, phaseB, phaseC, 0xB000, 0xB040, 0xB080);
}
