using System.Collections.ObjectModel;
using System.Globalization;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using ReactiveUI;
using SkiaSharp;
using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.App.ViewModels;

public sealed partial class MainViewModel
{
    private long _waveformReadId;
    private WaveformData? _displayedWaveformData;
    private string _waveformHoverText = string.Empty;
    private int? _hoveredWaveformSegment;
    private int _waveformTimeLabelStride = 2;

    /// <summary>仅稀疏时间文字，不改变20ms刻度和分界线。</summary>
    public void UpdateWaveformPlotWidth(double width)
    {
        var labelCount = Math.Clamp((int)(width / 110) + 1, 2, 7);
        var stride = (int)Math.Ceiling(6.0 / (labelCount - 1));
        if (stride == _waveformTimeLabelStride) return;
        _waveformTimeLabelStride = stride;
        if (_displayedWaveformData is { Points.Count: > 0 } data) ApplyWaveformTimeAxis(data);
    }

    private bool ShouldShowWaveformTimeLabel(double value)
    {
        var tickIndex = (int)Math.Round((value + 80) / 20);
        return tickIndex % _waveformTimeLabelStride == 0 || tickIndex == 6;
    }

    public ObservableCollection<RectangularSection> WaveformSections { get; } = new(
        Enumerable.Range(0, WaveformCatalog.SegmentCount).Select(index => new RectangularSection
        {
            Xi = -80 + index * 20,
            Xj = -60 + index * 20,
            Fill = new SolidColorPaint(new SKColor(70, 110, 200, (byte)(index % 2 == 0 ? 15 : 4))),
            Stroke = null,
        }));

    public string WaveformHoverText
    {
        get => _waveformHoverText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _waveformHoverText, value);
            this.RaisePropertyChanged(nameof(HasWaveformHover));
        }
    }

    public bool HasWaveformHover => WaveformHoverText.Length > 0;
    public int? HoveredWaveformSegment => _hoveredWaveformSegment;

    private void DisplayWaveform(WaveformData? data, bool preview = false)
    {
        _displayedWaveformData = data;
        ClearWaveformHover();
        _phaseASeries.Values = data?.Points.Select(point => new ObservablePoint(point.TimeMilliseconds, point.PhaseA)).ToArray() ?? [];
        _phaseBSeries.Values = data?.Points.Select(point => new ObservablePoint(point.TimeMilliseconds, point.PhaseB)).ToArray() ?? [];
        _phaseCSeries.Values = data?.Points.Select(point => new ObservablePoint(point.TimeMilliseconds, point.PhaseC)).ToArray() ?? [];
        if (data is not null && data.Points.Count > 0)
        {
            ApplyWaveformTimeAxis(data);
            // 已完成段只会追加，故预览轴范围随新的极值扩展；显隐不触发此方法。
            FixWaveformYAxis(data);
            var timing = data.Timing!;
            WaveformSummary = $"{(preview ? "读取预览" : "完整录波")} · {data.SampleRateHz:0.###} Hz · " +
                $"已完成 {data.Segments.Count}/6 段 · 每相 {data.Points.Count} 点 · " +
                $"{data.Calibration.FrameName}，Rate={data.Calibration.Rate:0.###}"; // · " +
                                                                                    // $"故障时间 {timing.FaultRecordTime:yyyy-MM-dd HH:mm:ss} + 软件补充 {timing.SoftwareMilliseconds} ms（非设备实测） · " +
                                                                                    // $"录波 {timing.WaveformStartTime:yyyy-MM-dd HH:mm:ss.fff}～{data.WaveformEndTime:HH:mm:ss.fffffff}";
        }
        else
        {
            WaveformXAxes[0].Labeler = value => value.ToString("0.###", CultureInfo.InvariantCulture);
            WaveformYAxes[0].MinLimit = null;
            WaveformYAxes[0].MaxLimit = null;
            WaveformSummary = "每完成一个时间段，即显示三相 AD 和分块 RMS";
        }
        this.RaisePropertyChanged(nameof(HasWaveformData));
        this.RaisePropertyChanged(nameof(HasNoWaveformData));
    }

    /// <summary>绘图区横坐标命中，左闭右开；与是否命中曲线以及相别显隐无关。</summary>
    public void UpdateWaveformHover(double relativeMilliseconds)
    {
        if (!double.IsFinite(relativeMilliseconds) || relativeMilliseconds < -80 || relativeMilliseconds >= 40)
        {
            ClearWaveformHover();
            return;
        }
        var index = (int)Math.Floor((relativeMilliseconds + 80) / 20);
        SetHoveredSegment(index);
        var segment = _displayedWaveformData?.Segments.FirstOrDefault(item => item.SegmentIndex == index);
        var range = WaveformCatalog.GetBlock(index, WaveformPhase.A).TimeRangeText;
        if (segment is null)
        {
            WaveformHoverText = $"第 {index + 1} 段：{range}\n该段尚未读取完成";
            return;
        }
        var local = Math.Clamp((int)Math.Round((relativeMilliseconds - (-80 + index * 20)) /
            WaveformCatalog.SampleIntervalMilliseconds), 0, WaveformCatalog.SamplesPerBlock - 1);
        var point = segment.Points[local];
        WaveformHoverText = $"第 {index + 1} 段：{range} · 每相64点\n" +
            $"A相 RMS：{segment.PhaseA.AmperesRms:0.0} A\n" +
            $"B相 RMS：{segment.PhaseB.AmperesRms:0.0} A\n" +
            $"C相 RMS：{segment.PhaseC.AmperesRms:0.0} A\n";// +
                                                          // $"采样时间：{_displayedWaveformData!.GetAbsoluteTime(point.TimeMilliseconds):yyyy-MM-dd HH:mm:ss.fffffff}\n" +
                                                          // $"采样值(AD)：A {point.PhaseA} / B {point.PhaseB} / C {point.PhaseC}";
    }

    public void ClearWaveformHover()
    {
        SetHoveredSegment(null);
        WaveformHoverText = string.Empty;
    }

    private void SetHoveredSegment(int? index)
    {
        if (_hoveredWaveformSegment == index) return;
        _hoveredWaveformSegment = index;
        for (var segment = 0; segment < WaveformSections.Count; segment++)
            WaveformSections[segment].Fill = new SolidColorPaint(new SKColor(70, 110, 200,
                (byte)(segment == index ? 48 : segment % 2 == 0 ? 15 : 4)));
        this.RaisePropertyChanged(nameof(HoveredWaveformSegment));
    }

    /// <summary>在UI同步上下文上投递；无UI上下文的服务测试直接执行，便于检查读块时序。</summary>
    private sealed class WaveformUiProgress(Action<WaveformReadProgress> callback) : IProgress<WaveformReadProgress>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;
        public void Report(WaveformReadProgress value)
        {
            if (_context is not null && SynchronizationContext.Current != _context)
                _context.Post(_ => callback(value), null);
            else
                callback(value);
        }
    }
}
