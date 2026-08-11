using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using ReactiveUI;
using WireLink.Core.Models;

namespace WireLink.App.ViewModels;

/// <summary>使用未经 int16 符号转换的 ushort 原始值绘制三相录波。</summary>
public sealed class WaveformRawChartViewModel : ViewModelBase
{
    private readonly LineSeries<ObservablePoint> _phaseASeries = CreateSeries("A 相原始值");
    private readonly LineSeries<ObservablePoint> _phaseBSeries = CreateSeries("B 相原始值");
    private readonly LineSeries<ObservablePoint> _phaseCSeries = CreateSeries("C 相原始值");
    private IReadOnlyList<WaveformRawPoint> _points = [];
    private string _summary = string.Empty;
    private bool _showPhaseA = true;
    private bool _showPhaseB = true;
    private bool _showPhaseC = true;

    public WaveformRawChartViewModel(WaveformData data)
    {
        RefreshSeriesSelection();
        Load(data);
    }

    public ObservableCollection<ISeries> Series { get; } = [];

    public Axis[] XAxes { get; } =
    [
        new Axis { Name = "相对故障时间 (ms)", Labeler = value => value.ToString("0.###") },
    ];

    public Axis[] YAxes { get; } =
    [
        new Axis
        {
            Name = "未转换原始值 (uint16)",
            MinLimit = 0,
            MaxLimit = ushort.MaxValue,
            Labeler = value => value.ToString("0"),
        },
    ];

    public IReadOnlyList<WaveformRawPoint> Points
    {
        get => _points;
        private set => this.RaiseAndSetIfChanged(ref _points, value);
    }

    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    public bool ShowPhaseA
    {
        get => _showPhaseA;
        set { this.RaiseAndSetIfChanged(ref _showPhaseA, value); RefreshSeriesSelection(); }
    }

    public bool ShowPhaseB
    {
        get => _showPhaseB;
        set { this.RaiseAndSetIfChanged(ref _showPhaseB, value); RefreshSeriesSelection(); }
    }

    public bool ShowPhaseC
    {
        get => _showPhaseC;
        set { this.RaiseAndSetIfChanged(ref _showPhaseC, value); RefreshSeriesSelection(); }
    }

    public void Load(WaveformData data)
    {
        Points = data.Points.Select(point => new WaveformRawPoint(
            point.SampleIndex,
            point.TimeMilliseconds,
            unchecked((ushort)point.PhaseA),
            unchecked((ushort)point.PhaseB),
            unchecked((ushort)point.PhaseC))).ToArray();

        _phaseASeries.Values = Points.Select(point => new ObservablePoint(point.TimeMilliseconds, point.PhaseA)).ToArray();
        _phaseBSeries.Values = Points.Select(point => new ObservablePoint(point.TimeMilliseconds, point.PhaseB)).ToArray();
        _phaseCSeries.Values = Points.Select(point => new ObservablePoint(point.TimeMilliseconds, point.PhaseC)).ToArray();
        Summary = $"{data.SampleRateHz:0.###} Hz · 每相 {Points.Count} 点 · " +
                  "显示范围 0～65535；原有负数会显示为接近 65535 的补码原值";
    }

    private static LineSeries<ObservablePoint> CreateSeries(string name) => new()
    {
        Name = name,
        Values = Array.Empty<ObservablePoint>(),
        Fill = null,
        GeometrySize = 0,
        LineSmoothness = 0,
    };

    private void RefreshSeriesSelection()
    {
        Series.Clear();
        if (ShowPhaseA) Series.Add(_phaseASeries);
        if (ShowPhaseB) Series.Add(_phaseBSeries);
        if (ShowPhaseC) Series.Add(_phaseCSeries);
    }
}

public sealed record WaveformRawPoint(
    int SampleIndex,
    double TimeMilliseconds,
    ushort PhaseA,
    ushort PhaseB,
    ushort PhaseC);
