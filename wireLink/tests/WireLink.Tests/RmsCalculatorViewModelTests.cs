using System.Reactive.Threading.Tasks;
using WireLink.App.ViewModels;

namespace WireLink.Tests;

public sealed class RmsCalculatorViewModelTests
{
    [Fact]
    public void User_64_point_example_defaults_to_frame_two_rate_one_point_five()
    {
        var vm = new RmsCalculatorViewModel { SamplesText = RmsCalculatorViewModel.ExampleSamples };
        vm.Calculate();
        var result = Assert.IsType<RmsCalculationResult>(vm.Result);
        Assert.False(vm.HasError);
        Assert.Equal(64, result.SampleCount);
        Assert.Equal(24291643, result.SumOfSquares);
        Assert.Equal(616.0819116602921, result.AdRms, 10);
        Assert.Equal("1.5", vm.RateText);
        Assert.Equal(1.5, result.Rate);
        Assert.Equal(402.6152866686003, result.UnroundedAmperesRms, 10);
        Assert.Equal(402.6, result.AmperesRms);
        Assert.Equal("N = 64 点", vm.CountText);
        Assert.Equal("616.081912 AD", vm.AdRmsText);
        Assert.Equal("402.6 A", vm.AmperesRmsText);
        Assert.Contains("√(24291643 / 64)", vm.CalculationSteps);
        Assert.Contains("× (10000 / 22953) × 1.5", vm.CalculationSteps);
    }

    [Theory]
    [InlineData("1", 268.4)]
    [InlineData("1.5", 402.6)]
    [InlineData("2", 536.8)]
    public void Rate_is_applied_after_ad_rms_calculation(string rate, double expected)
    {
        var vm = new RmsCalculatorViewModel { RateText = rate, SamplesText = RmsCalculatorViewModel.ExampleSamples };
        vm.Calculate();
        Assert.Equal(616.0819116602921, vm.Result!.AdRms, 10);
        Assert.Equal(expected, vm.Result.AmperesRms);
    }

    [Fact]
    public void Mixed_separators_and_signed_values_use_actual_input_count()
    {
        var vm = new RmsCalculatorViewModel { SamplesText = "  1\r\n-1\t+1, -1;1，-1；1 -1  " };
        vm.Calculate();
        Assert.False(vm.HasError);
        Assert.Equal(8, vm.Result!.SampleCount);
        Assert.Equal(8, vm.Result.SumOfSquares);
        Assert.Equal(1, vm.Result.AdRms);
        Assert.Equal(0.7, vm.Result.AmperesRms);
    }

    [Theory]
    [InlineData("0 0 0 0", 0)]
    [InlineData("-32768 -32768", 32768)]
    [InlineData("32767", 32767)]
    public void Signed_boundaries_and_zero_are_valid_samples(string samples, double expectedRms)
    {
        var vm = new RmsCalculatorViewModel { SamplesText = samples };
        vm.Calculate();
        Assert.False(vm.HasError);
        Assert.Equal(expectedRms, vm.Result!.AdRms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,5")]
    public void Invalid_rate_does_not_produce_a_result(string rate)
    {
        var vm = new RmsCalculatorViewModel { RateText = rate, SamplesText = "1 -1" };
        vm.Calculate();
        Assert.Null(vm.Result);
        Assert.True(vm.HasError);
        Assert.Contains("Rate", vm.Error);
    }

    [Theory]
    [InlineData("1\n65216")]
    [InlineData("1\n-32769")]
    [InlineData("1\n1.5")]
    [InlineData("1\nabc")]
    public void Invalid_sample_reports_its_position_instead_of_silently_skipping_it(string samples)
    {
        var vm = new RmsCalculatorViewModel { SamplesText = samples };
        vm.Calculate();
        Assert.Null(vm.Result);
        Assert.Contains("第2项", vm.Error);
        Assert.Contains("-32768～32767", vm.Error);
    }

    [Fact]
    public void Empty_input_and_overflow_are_reported()
    {
        var vm = new RmsCalculatorViewModel();
        vm.Calculate();
        Assert.True(vm.HasError);
        Assert.Null(vm.Result);
        vm.RateText = "1e308";
        vm.SamplesText = "32767";
        vm.Calculate();
        Assert.Null(vm.Result);
        Assert.Contains("超出可计算范围", vm.Error);
    }

    [Fact]
    public async Task Changing_inputs_clears_stale_result_and_clear_keeps_rate()
    {
        var vm = new RmsCalculatorViewModel { RateText = "1.5" };
        await vm.LoadExampleCommand.Execute().ToTask();
        Assert.Equal(402.6, vm.Result!.AmperesRms);
        vm.RateText = "2";
        Assert.False(vm.HasResult);
        Assert.Equal("—", vm.AmperesRmsText);
        vm.Calculate();
        Assert.Equal(536.8, vm.Result!.AmperesRms);
        vm.SamplesText = "1 -1";
        Assert.False(vm.HasResult);
        await vm.ClearCommand.Execute().ToTask();
        Assert.Equal("2", vm.RateText);
        Assert.Empty(vm.SamplesText);
        Assert.Empty(vm.CalculationSteps);
    }
}
