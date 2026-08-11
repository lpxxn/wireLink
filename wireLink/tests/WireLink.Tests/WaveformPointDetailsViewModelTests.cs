using WireLink.App.ViewModels;
using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.Tests;

public sealed class WaveformPointDetailsViewModelTests
{
    [Fact]
    public void No_waveform_data_shows_empty_read_hint()
    {
        var viewModel = new WaveformPointDetailsViewModel(null);

        Assert.Empty(viewModel.Rows);
        Assert.False(viewModel.HasData);
        Assert.Null(viewModel.CurrentData);
        Assert.Contains("尚未读取完整录波数据", viewModel.Summary);
    }

    [Fact]
    public void Details_preserve_all_384_points_addresses_signed_values_and_raw_hex()
    {
        var points = Enumerable.Range(0, WaveformCatalog.PointsPerPhase)
            .Select(CreatePoint)
            .ToArray();
        var data = new WaveformData(
            new DateTimeOffset(2026, 8, 10, 12, 34, 56, TimeSpan.FromHours(8)),
            WaveformCatalog.SampleRateHz,
            points,
            1,
            2,
            3);

        var viewModel = new WaveformPointDetailsViewModel(data);

        Assert.Equal(384, viewModel.Rows.Count);
        Assert.True(viewModel.HasData);
        Assert.Same(data, viewModel.CurrentData);
        Assert.Contains("每相：384 点", viewModel.Summary);
        Assert.Contains("三相原始值：1152 个", viewModel.Summary);

        var first = viewModel.Rows[0];
        Assert.Equal(1, first.SampleNumber);
        Assert.Equal("-80～-60 ms", first.SegmentText);
        Assert.Equal(1, first.SegmentSampleNumber);
        Assert.Equal(-80, first.TimeMilliseconds);
        Assert.Equal("B000H", first.PhaseAAddress);
        Assert.Equal("FF40H", first.PhaseAHex);
        Assert.Equal(65344, first.PhaseARawDecimal);
        Assert.Equal(-192, first.PhaseAValue);

        var last = viewModel.Rows[^1];
        Assert.Equal(384, last.SampleNumber);
        Assert.Equal("20～40 ms", last.SegmentText);
        Assert.Equal(64, last.SegmentSampleNumber);
        Assert.Equal(39.6875, last.TimeMilliseconds);
        Assert.Equal("B53FH", last.PhaseAAddress);
        Assert.Equal("B57FH", last.PhaseBAddress);
        Assert.Equal("B5BFH", last.PhaseCAddress);
        Assert.Equal("00BFH", last.PhaseAHex);
        Assert.Equal("FE81H", last.PhaseBHex);
        Assert.Equal("0000H", last.PhaseCHex);
        Assert.Equal(191, last.PhaseARawDecimal);
        Assert.Equal(65153, last.PhaseBRawDecimal);
        Assert.Equal(0, last.PhaseCRawDecimal);

        Assert.Equal(
            [1,65,129,193,257,321],
            viewModel.Rows.Where(row => row.IsSegmentStart).Select(row => row.SampleNumber));
    }

    private static WaveformPoint CreatePoint(int sampleIndex)
    {
        var segmentIndex = sampleIndex / WaveformCatalog.SamplesPerBlock;
        var segmentSampleIndex = sampleIndex % WaveformCatalog.SamplesPerBlock;
        var segmentBase = 0xB000 + segmentIndex * 0x100;
        return new WaveformPoint(
            sampleIndex,
            segmentIndex,
            segmentSampleIndex,
            WaveformCatalog.GetTimeMilliseconds(sampleIndex),
            checked((short)(sampleIndex - 192)),
            checked((short)-sampleIndex),
            0,
            checked((ushort)(segmentBase + segmentSampleIndex)),
            checked((ushort)(segmentBase + 0x40 + segmentSampleIndex)),
            checked((ushort)(segmentBase + 0x80 + segmentSampleIndex)));
    }
}
