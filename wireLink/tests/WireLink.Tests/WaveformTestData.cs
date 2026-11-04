using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.Tests;

internal static class WaveformTestData
{
    public static WaveformData Create(DateTimeOffset readAt, double sampleRate,
        IReadOnlyList<WaveformPoint> points, WaveformCalibration calibration)
    {
        var segments = points.GroupBy(point => point.SegmentIndex)
            .Where(group => group.Count() == WaveformCatalog.SamplesPerBlock)
            .Select(group =>
            {
                var sorted = group.OrderBy(point => point.SegmentSampleIndex).ToArray();
                return new WaveformSegmentResult(
                    WaveformBlockResult.Create(WaveformCatalog.GetBlock(group.Key, WaveformPhase.A), sorted.Select(point => point.PhaseA), calibration),
                    WaveformBlockResult.Create(WaveformCatalog.GetBlock(group.Key, WaveformPhase.B), sorted.Select(point => point.PhaseB), calibration),
                    WaveformBlockResult.Create(WaveformCatalog.GetBlock(group.Key, WaveformPhase.C), sorted.Select(point => point.PhaseC), calibration));
            }).ToArray();
        return new WaveformData(readAt, sampleRate, points, segments, calibration);
    }
}
