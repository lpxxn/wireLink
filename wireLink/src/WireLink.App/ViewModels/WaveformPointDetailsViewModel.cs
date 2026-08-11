using ReactiveUI;
using WireLink.Core.Models;

namespace WireLink.App.ViewModels;

/// <summary>录波原始点明细窗口的数据快照。</summary>
public sealed class WaveformPointDetailsViewModel : ViewModelBase
{
    private IReadOnlyList<WaveformPointDetailRow> _rows = [];
    private string _summary = string.Empty;
    private WaveformData? _currentData;

    public WaveformPointDetailsViewModel(WaveformData? data) => Load(data);

    public IReadOnlyList<WaveformPointDetailRow> Rows
    {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    public WaveformData? CurrentData
    {
        get => _currentData;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentData, value);
            this.RaisePropertyChanged(nameof(HasData));
        }
    }

    public bool HasData => CurrentData is not null;

    public void Load(WaveformData? data)
    {
        CurrentData = data;
        if (data is null)
        {
            Rows = [];
            Summary = "尚未读取完整录波数据，请返回录波页面点击“立即读取”。";
            return;
        }

        Rows = data.Points.Select(WaveformPointDetailRow.FromPoint).ToArray();
        Summary = $"读取时间：{data.ReadAt:yyyy-MM-dd HH:mm:ss}　采样率：{data.SampleRateHz:0.###} Hz　" +
                  $"每相：{data.Points.Count} 点　三相原始值：{data.Points.Count * 3} 个";
    }
}

public sealed record WaveformPointDetailRow(
    int SampleNumber,
    string SegmentText,
    int SegmentSampleNumber,
    double TimeMilliseconds,
    string PhaseAAddress,
    string PhaseAHex,
    ushort PhaseARawDecimal,
    short PhaseAValue,
    string PhaseBAddress,
    string PhaseBHex,
    ushort PhaseBRawDecimal,
    short PhaseBValue,
    string PhaseCAddress,
    string PhaseCHex,
    ushort PhaseCRawDecimal,
    short PhaseCValue)
{
    /// <summary>每个 64 点时间段的第一行，用于在明细表中标出六个数据块的边界。</summary>
    public bool IsSegmentStart => SegmentSampleNumber == 1;

    public static WaveformPointDetailRow FromPoint(WaveformPoint point)
    {
        var segmentStart = -80 + point.SegmentIndex * 20;
        return new WaveformPointDetailRow(
            point.SampleIndex + 1,
            $"{segmentStart}～{segmentStart + 20} ms",
            point.SegmentSampleIndex + 1,
            point.TimeMilliseconds,
            Address(point.PhaseAAddress),
            RawHex(point.PhaseA),
            RawDecimal(point.PhaseA),
            point.PhaseA,
            Address(point.PhaseBAddress),
            RawHex(point.PhaseB),
            RawDecimal(point.PhaseB),
            point.PhaseB,
            Address(point.PhaseCAddress),
            RawHex(point.PhaseC),
            RawDecimal(point.PhaseC),
            point.PhaseC);
    }

    private static string Address(ushort value) => $"{value:X4}H";

    private static string RawHex(short value) => $"{unchecked((ushort)value):X4}H";

    private static ushort RawDecimal(short value) => unchecked((ushort)value);
}
