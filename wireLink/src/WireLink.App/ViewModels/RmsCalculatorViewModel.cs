using System.Globalization;
using System.Text.RegularExpressions;
using ReactiveUI;
using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.App.ViewModels;

/// <summary>独立的有符号AD均方根计算器，不依赖设备连接或当前录波结果。</summary>
public sealed class RmsCalculatorViewModel : ViewModelBase
{
    private string _rateText = "1.5";
    private string _samplesText = string.Empty;
    private string _error = string.Empty;
    private RmsCalculationResult? _result;

    public RmsCalculatorViewModel()
    {
        CalculateCommand = ReactiveCommand.Create(Calculate);
        ClearCommand = ReactiveCommand.Create(() => { SamplesText = string.Empty; });
        LoadExampleCommand = ReactiveCommand.Create(() =>
        {
            SamplesText = ExampleSamples;
            Calculate();
        });
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CalculateCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ClearCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> LoadExampleCommand { get; }

    public string RateText
    {
        get => _rateText;
        set
        {
            this.RaiseAndSetIfChanged(ref _rateText, value);
            ClearResult();
        }
    }

    public string SamplesText
    {
        get => _samplesText;
        set
        {
            this.RaiseAndSetIfChanged(ref _samplesText, value);
            ClearResult();
        }
    }

    public RmsCalculationResult? Result
    {
        get => _result;
        private set
        {
            this.RaiseAndSetIfChanged(ref _result, value);
            foreach (var property in new[] { nameof(HasResult), nameof(CountText), nameof(AdRmsText),
                         nameof(AmperesRmsText), nameof(CalculationSteps) })
                this.RaisePropertyChanged(property);
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;
    public bool HasResult => Result is not null;
    public string CountText => Result is { } result ? $"N = {result.SampleCount} 点" : "尚未计算";
    public string AdRmsText => Result is { } result ? $"{result.AdRms.ToString("0.000000", CultureInfo.InvariantCulture)} AD" : "—";
    public string AmperesRmsText => Result is { } result ? $"{result.AmperesRms.ToString("0.0", CultureInfo.InvariantCulture)} A" : "—";

    public string CalculationSteps => Result is not { } result ? string.Empty :
        $"平方和 ΣADᵢ² = {Format(result.SumOfSquares)}；N = {result.SampleCount}\n" +
        $"AD-RMS = √({Format(result.SumOfSquares)} / {result.SampleCount}) = {Format(result.AdRms)} AD\n" +
        $"RMS(A) = {Format(result.AdRms)} × (10000 / 22953) × {Format(result.Rate)} " +
        $"= {Format(result.UnroundedAmperesRms)} A → {result.AmperesRms.ToString("0.0", CultureInfo.InvariantCulture)} A";

    public void Calculate()
    {
        ClearResult();
        if (!double.TryParse(RateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
            !double.IsFinite(rate) || rate <= 0)
        {
            Error = "请输入大于0的有效 Rate，例如1、1.5或2（小数使用英文句点）。";
            return;
        }
        if (string.IsNullOrWhiteSpace(SamplesText))
        {
            Error = "请输入有符号AD采样数据；每行一个值，或使用空格、逗号、分号分隔。";
            return;
        }

        var tokens = Regex.Split(SamplesText.Trim(), @"[\s,;，；]+");
        var samples = new short[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (short.TryParse(tokens[index], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out samples[index])) continue;
            var token = tokens[index].Length > 24 ? tokens[index][..24] + "…" : tokens[index];
            Error = $"第{index + 1}项“{token}”不是有效的有符号AD整数（-32768～32767）。";
            return;
        }

        var sumOfSquares = samples.Sum(value => (double)value * value);
        var adRms = WaveformSampleDecoder.CalculateRms(samples);
        var amperesPerAd = WaveformCalibration.ReferenceCurrentAmperes /
                           WaveformCalibration.ReferenceAdValue * rate;
        var amperesRms = adRms * amperesPerAd;
        if (!double.IsFinite(amperesPerAd) || !double.IsFinite(amperesRms))
        {
            Error = "Rate过大，安培RMS超出可计算范围，请减小Rate。";
            return;
        }
        Result = new RmsCalculationResult(samples.Length, sumOfSquares, adRms, rate,
            amperesRms, WaveformCalibration.RoundAmperes(amperesRms));
    }

    private void ClearResult()
    {
        Result = null;
        Error = string.Empty;
    }

    private static string Format(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>用户给出的完整64点单相示例，未经平滑或换算。</summary>
    public const string ExampleSamples = """
433
354
273
191
107
25
-60
-144
-223
-297
-369
-440
-506
-570
-630
-688
-737
-782
-820
-849
-871
-883
-882
-872
-853
-827
-793
-750
-700
-643
-579
-510
-435
-358
-277
-196
-112
-25
58
141
222
296
368
438
505
569
629
685
737
781
819
849
872
884
883
875
859
834
802
760
708
649
583
511
""";
}

public sealed record RmsCalculationResult(int SampleCount, double SumOfSquares, double AdRms,
    double Rate, double UnroundedAmperesRms, double AmperesRms);
