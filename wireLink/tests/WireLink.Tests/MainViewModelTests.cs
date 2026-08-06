using System.Reactive.Threading.Tasks;
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

    private static MainViewModel CreateViewModel(
        IReadOnlyList<string> ports,
        AppSettings settings,
        IModbusRtuClient? client=null,
        IProtocolTrace? trace=null)
    {
        client??=new FakeClient();
        trace??=new RecordingProtocolTrace();
        return new MainViewModel(
            client,
            new FakePortCatalog(ports),
            new FakeDeviceDataService(),
            new FakeFaultRecordService(),
            new FakeWaveformDataService(),
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

    private sealed class FakeSettingsService : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken=default)=>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings,CancellationToken cancellationToken=default)=>
            Task.CompletedTask;
    }
}
