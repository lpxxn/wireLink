using System.Reactive.Threading.Tasks;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using WireLink.App.ViewModels;
using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Registers;
using WireLink.Core.Services;

namespace WireLink.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task No_available_ports_and_no_manual_input_disables_open_button()
    {
        await using var viewModel=CreateViewModel([],new AppSettings());

        Assert.Empty(viewModel.PortNames);
        Assert.Equal(string.Empty,viewModel.PortName);
        Assert.False(viewModel.CanToggleSerial);
        Assert.Contains("未检测到可用串口",viewModel.Notice);
    }

    [Fact]
    public async Task Manually_entered_port_does_not_have_to_exist_in_dropdown()
    {
        await using var viewModel=CreateViewModel(["COM11"],new AppSettings());

        Assert.Equal(["COM11"],viewModel.PortNames);
        Assert.Equal("COM11",viewModel.PortName);
        viewModel.PortName="COM99";
        Assert.True(viewModel.CanToggleSerial);
        Assert.Equal("COM99",viewModel.PortName);
    }

    [Fact]
    public async Task Empty_timeout_disables_opening_serial_until_a_value_is_entered_again()
    {
        await using var viewModel=CreateViewModel(["COM11"],new AppSettings(PortName:"COM11"));

        viewModel.ReadTimeoutMilliseconds=null;

        Assert.Null(viewModel.ReadTimeoutMilliseconds);
        Assert.False(viewModel.CanToggleSerial);

        viewModel.ReadTimeoutMilliseconds=1200;

        Assert.True(viewModel.CanToggleSerial);
    }

    [Fact]
    public async Task Empty_main_numeric_fields_disable_only_the_operations_that_need_them()
    {
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            deviceService:new ConnectedDeviceDataService());
        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();

        Assert.True(viewModel.CanRead);
        Assert.True(viewModel.CanAutoRefresh);
        Assert.True(viewModel.CanReadFault);

        viewModel.RefreshSeconds=null;
        Assert.Null(viewModel.RefreshSeconds);
        Assert.False(viewModel.CanAutoRefresh);
        Assert.True(viewModel.CanRead);

        viewModel.FaultRecordIndex=null;
        Assert.Null(viewModel.FaultRecordIndex);
        Assert.False(viewModel.CanReadFault);
        Assert.True(viewModel.CanRead);

        viewModel.FaultRecordIndex=0;
        viewModel.FaultDelayMilliseconds=null;
        Assert.Null(viewModel.FaultDelayMilliseconds);
        Assert.False(viewModel.CanReadFault);

        viewModel.RefreshSeconds=3;
        viewModel.FaultDelayMilliseconds=100;
        Assert.True(viewModel.CanAutoRefresh);
        Assert.True(viewModel.CanReadFault);
    }

    [Fact]
    public async Task Empty_device_address_disables_connection_and_read_operations()
    {
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            deviceService:new ConnectedDeviceDataService());
        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        Assert.True(viewModel.IsDeviceConnected);

        viewModel.DeviceAddress=null;

        Assert.False(viewModel.IsDeviceConnected);
        Assert.False(viewModel.CanTest);
        Assert.False(viewModel.CanRead);

        viewModel.DeviceAddress=1;
        Assert.True(viewModel.CanTest);
        Assert.False(viewModel.CanRead);
    }

    [Fact]
    public async Task Open_failure_is_exposed_as_a_friendly_visible_notice()
    {
        var trace=new RecordingProtocolTrace();
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            new FakeClient(new UnauthorizedAccessException("access denied")),
            trace);
        ErrorDialogRequest? dialogRequest=null;
        viewModel.ErrorDialogRequested+=(_,request)=>dialogRequest=request;

        await viewModel.ToggleSerialCommand.Execute().ToTask();

        Assert.False(viewModel.IsSerialOpen);
        Assert.Equal("打开串口失败：串口权限不足或被占用",viewModel.Notice);
        Assert.Equal("打开串口失败",dialogRequest?.Title);
        Assert.Equal("串口权限不足或被占用",dialogRequest?.Message);
        Assert.Single(trace.ErrorMessages);
    }

    [Fact]
    public async Task Waveform_x_axis_uses_dashed_separators_every_20_milliseconds()
    {
        await using var viewModel=CreateViewModel([],new AppSettings());

        var axis=Assert.Single(viewModel.WaveformXAxes);
        Assert.Equal(-80,axis.MinLimit);
        Assert.Equal(40,axis.MaxLimit);
        Assert.Equal(20,axis.MinStep);
        Assert.True(axis.ForceStepToMin);
        var separatorPaint=Assert.IsType<SolidColorPaint>(axis.SeparatorsPaint);
        Assert.IsType<DashEffect>(separatorPaint.PathEffect);
    }

    [Fact]
    public async Task Completed_waveform_status_is_not_overwritten_by_a_late_18_of_18_progress_callback()
    {
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            deviceService:new ConnectedDeviceDataService(),
            waveformService:new CompletedWaveformDataService());

        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        await viewModel.ReadWaveformCommand.Execute().ToTask();

        // 模拟 UI 消息队列：录波服务返回后，最后一个 18/18 进度才到达。
        await Task.Delay(100);

        Assert.StartsWith("完整录波读取完成",viewModel.WaveformProgressText);
    }

    [Fact]
    public async Task Waveform_y_axis_is_fixed_from_all_phases_and_does_not_change_when_phases_are_hidden()
    {
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            deviceService:new ConnectedDeviceDataService(),
            waveformService:new CompletedWaveformDataService());

        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        await viewModel.ReadWaveformCommand.Execute().ToTask();

        var axis=Assert.Single(viewModel.WaveformYAxes);
        Assert.Equal(-4,axis.MinLimit);
        Assert.Equal(4,axis.MaxLimit);

        viewModel.ShowPhaseB=false;
        viewModel.ShowPhaseC=false;
        Assert.Equal(-4,axis.MinLimit);
        Assert.Equal(4,axis.MaxLimit);

        viewModel.ShowPhaseA=false;
        viewModel.ShowPhaseC=true;
        Assert.Equal(-4,axis.MinLimit);
        Assert.Equal(4,axis.MaxLimit);
    }

    private static MainViewModel CreateViewModel(
        IReadOnlyList<string> ports,
        AppSettings settings,
        IModbusRtuClient? client=null,
        IProtocolTrace? trace=null,
        IDeviceDataService? deviceService=null,
        IWaveformDataService? waveformService=null)
    {
        client??=new FakeClient();
        trace??=new RecordingProtocolTrace();
        return new MainViewModel(
            client,
            new FakePortCatalog(ports),
            deviceService ?? new FakeDeviceDataService(),
            new FakeFaultRecordService(),
            waveformService ?? new FakeWaveformDataService(),
            new FakeSettingsService(),
            trace,
            settings);
    }

    private sealed class FakePortCatalog(IReadOnlyList<string> ports) : ISerialPortCatalog
    {
        public IReadOnlyList<string> GetPortNames()=>ports;
    }

    private sealed class FakeClient(Exception? openException=null) : IModbusRtuClient
    {
        public bool IsOpen { get; private set; }

        public ValueTask OpenAsync(SerialConnectionOptions options,CancellationToken cancellationToken=default)
        {
            if(openException is not null) return ValueTask.FromException(openException);
            IsOpen=true;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken=default)
        {
            IsOpen=false;
            return ValueTask.CompletedTask;
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress,ushort startAddress,ushort count,
            CancellationToken cancellationToken=default)=>Task.FromResult(Array.Empty<ushort>());

        public Task WriteSingleRegisterAsync(byte slaveAddress,ushort address,ushort value,
            CancellationToken cancellationToken=default)=>Task.CompletedTask;

        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class FakeDeviceDataService : IDeviceDataService
    {
        public Task<bool> TestConnectionAsync(byte slaveAddress,CancellationToken cancellationToken=default)=>
            Task.FromResult(false);

        public Task<DataReadResult> ReadAsync(byte slaveAddress,WordOrder wordOrder,
            BreakerSeries controllerSeries,CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));
    }

    private sealed class ConnectedDeviceDataService : IDeviceDataService
    {
        public Task<bool> TestConnectionAsync(byte slaveAddress,CancellationToken cancellationToken=default)=>
            Task.FromResult(true);

        public Task<DataReadResult> ReadAsync(byte slaveAddress,WordOrder wordOrder,
            BreakerSeries controllerSeries,CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));
    }

    private sealed class FakeFaultRecordService : IFaultRecordService
    {
        public Task<DataReadResult> ReadAsync(byte slaveAddress,FaultRecordType type,byte recordIndex,
            WordOrder wordOrder,BreakerSeries controllerSeries,TimeSpan readyDelay,
            CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));
    }

    private sealed class FakeWaveformDataService : IWaveformDataService
    {
        public Task<WaveformData> ReadAsync(byte slaveAddress,
            IProgress<WaveformReadProgress>? progress=null,
            CancellationToken cancellationToken=default)=>
            Task.FromException<WaveformData>(new InvalidOperationException("测试未配置录波数据"));
    }

    private sealed class CompletedWaveformDataService : IWaveformDataService
    {
        public Task<WaveformData> ReadAsync(byte slaveAddress,
            IProgress<WaveformReadProgress>? progress=null,
            CancellationToken cancellationToken=default)
        {
            var lastBlock=WaveformCatalog.Blocks[WaveformCatalog.TotalBlocks - 1];
            _=Task.Run(async () =>
            {
                await Task.Delay(25,cancellationToken);
                progress?.Report(new WaveformReadProgress(
                    WaveformCatalog.TotalBlocks,
                    WaveformCatalog.TotalBlocks,
                    lastBlock));
            },cancellationToken);

            IReadOnlyList<WaveformPoint> points=
            [
                new WaveformPoint(0,0,0,-80,1,2,3,0xAC00,0xAC80,0xAD00),
            ];
            return Task.FromResult(new WaveformData(
                new DateTimeOffset(2026,8,11,12,0,0,TimeSpan.FromHours(8)),
                3200,
                points,
                1,
                2,
                3));
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken=default)=>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings,CancellationToken cancellationToken=default)=>
            Task.CompletedTask;
    }
}
