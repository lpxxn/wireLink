using System.Reactive.Threading.Tasks;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
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
        Assert.True(viewModel.CanReadWaveform);

        viewModel.RefreshSeconds=null;
        Assert.Null(viewModel.RefreshSeconds);
        Assert.False(viewModel.CanAutoRefresh);
        Assert.True(viewModel.CanRead);

        viewModel.FaultRecordIndex=null;
        Assert.Null(viewModel.FaultRecordIndex);
        Assert.False(viewModel.CanReadFault);
        Assert.True(viewModel.CanReadWaveform);
        Assert.True(viewModel.CanRead);

        viewModel.FaultRecordIndex=0;
        viewModel.FaultDelayMilliseconds=null;
        Assert.Null(viewModel.FaultDelayMilliseconds);
        Assert.False(viewModel.CanReadFault);
        Assert.False(viewModel.CanReadWaveform);

        viewModel.RefreshSeconds=3;
        viewModel.FaultDelayMilliseconds=100;
        Assert.True(viewModel.CanAutoRefresh);
        Assert.True(viewModel.CanReadFault);
        Assert.True(viewModel.CanReadWaveform);
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
        Assert.Equal("录波时间",axis.Name);
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
    public async Task Waveform_read_runs_timestamp_offset_and_waveform_steps_in_that_order()
    {
        var calls=new List<string>();
        var faultService=new FakeFaultRecordService(()=>calls.Add("故障时间"));
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            deviceService:new ConnectedDeviceDataService(),
            faultService:faultService,
            waveformTimeOffsetStore:new FakeWaveformTimeOffsetStore(123,()=>calls.Add("稳定毫秒")),
            waveformService:new CompletedWaveformDataService(()=>calls.Add("1552和18块")));

        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        viewModel.SelectedFaultRecordType=viewModel.FaultRecordTypes.Single(
            option=>option.Value==FaultRecordType.StateChange);
        viewModel.FaultRecordIndex=15;
        await viewModel.ReadWaveformCommand.Execute().ToTask();

        Assert.Equal(["故障时间","稳定毫秒","1552和18块"],calls);
        Assert.Equal(FaultRecordType.Fault,faultService.LastTimestampRecordType);
        Assert.Equal((byte)0,faultService.LastTimestampRecordIndex);
        Assert.Equal(FaultRecordType.Fault,viewModel.CurrentWaveformData?.Timing?.RecordType);
        Assert.Equal((byte)0,viewModel.CurrentWaveformData?.Timing?.RecordIndex);
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
        Assert.Equal(-21600,axis.MinLimit);
        Assert.Equal(21600,axis.MaxLimit);
        Assert.Equal("电流 (A)",axis.Name);
        var phaseA=Assert.IsType<LineSeries<ObservablePoint>>(viewModel.WaveformSeries[0]);
        var phaseAPoint=Assert.Single(phaseA.Values!.Cast<ObservablePoint>());
        Assert.Equal(20000,phaseAPoint.Y);
        Assert.NotNull(phaseA.YToolTipLabelFormatter);
        Assert.Contains("20000",viewModel.WaveformSummary);
        Assert.Contains("框III，Rate=2",viewModel.WaveformSummary);
        Assert.DoesNotContain("框架等级 2",viewModel.WaveformSummary);
        var xAxis=Assert.Single(viewModel.WaveformXAxes);
        Assert.Equal("14:30:01.123",xAxis.Labeler!(-80));
        Assert.Equal("14:30:01.243",xAxis.Labeler!(40));
        Assert.Contains("软件补充 123 ms",viewModel.WaveformSummary);
        Assert.Contains("非设备实测",viewModel.WaveformSummary);
        Assert.Contains("录波 2026-07-22 14:30:01.123～14:30:01.1230000",viewModel.WaveformSummary);
        Assert.NotNull(phaseA.XToolTipLabelFormatter);

        viewModel.ShowPhaseB=false;
        viewModel.ShowPhaseC=false;
        Assert.Equal(-21600,axis.MinLimit);
        Assert.Equal(21600,axis.MaxLimit);

        viewModel.ShowPhaseA=false;
        viewModel.ShowPhaseC=true;
        Assert.Equal(-21600,axis.MinLimit);
        Assert.Equal(21600,axis.MaxLimit);
    }

    [Fact]
    public async Task Failed_followup_waveform_read_preserves_last_complete_ampere_waveform()
    {
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            deviceService:new ConnectedDeviceDataService(),
            waveformService:new SucceedThenFailWaveformDataService());
        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();

        await viewModel.ReadWaveformCommand.Execute().ToTask();
        var firstData=viewModel.CurrentWaveformData;
        var firstSummary=viewModel.WaveformSummary;
        await viewModel.ReadWaveformCommand.Execute().ToTask();

        Assert.Same(firstData,viewModel.CurrentWaveformData);
        Assert.Equal(firstSummary,viewModel.WaveformSummary);
        Assert.Contains("读取录波数据失败",viewModel.Notice);
    }

    [Fact]
    public async Task Missing_fault_timestamp_shows_dialog_stops_before_waveform_and_preserves_old_data()
    {
        var waveformService=new CountingWaveformDataService();
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10"),
            deviceService:new ConnectedDeviceDataService(),
            waveformService:waveformService,
            faultService:new SucceedThenFailTimestampFaultRecordService());
        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        await viewModel.ReadWaveformCommand.Execute().ToTask();
        var previous=viewModel.CurrentWaveformData;
        ErrorDialogRequest? dialog=null;
        viewModel.ErrorDialogRequested+=(_,request)=>dialog=request;

        await viewModel.ReadWaveformCommand.Execute().ToTask();

        Assert.Same(previous,viewModel.CurrentWaveformData);
        Assert.Equal(1,waveformService.Calls);
        Assert.Equal("无法读取录波数据",dialog?.Title);
        Assert.Contains("未读取到有效的故障记录时间",dialog?.Message);
        Assert.Contains("未读取到有效的故障记录时间",viewModel.WaveformProgressText);
        Assert.Contains("未读取到有效的故障记录时间",viewModel.Notice);
    }

    [Fact]
    public async Task Switching_connected_device_type_keeps_serial_open_and_requires_new_connection_test()
    {
        var deviceService=new RecordingDeviceDataService();
        await using var viewModel=CreateViewModel(
            ["COM10"],new AppSettings(PortName:"COM10"),deviceService:deviceService);
        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        viewModel.AutoRefresh=true;
        viewModel.SelectedDataTabIndex=2;

        viewModel.SelectedDevice=viewModel.DeviceOptions.Single(
            option=>option.Value==DeviceType.MoldedCaseCircuitBreaker);

        Assert.True(viewModel.IsSerialOpen);
        Assert.False(viewModel.IsDeviceConnected);
        Assert.False(viewModel.AutoRefresh);
        Assert.Equal(0,viewModel.SelectedDataTabIndex);
        Assert.Equal([DeviceType.FrameController],deviceService.TestedTypes);
        Assert.True(viewModel.IsMoldedCaseCircuitBreaker);
        Assert.False(viewModel.IsFrameController);
        Assert.Empty(viewModel.FaultRows);
        Assert.Contains(viewModel.DeviceRows.SelectMany(RowItems),item=>item.Name=="A 相电流");
        var leakageCurrent=Assert.Single(viewModel.ProtectionRows.SelectMany(RowItems),
            item=>item.Name=="漏电电流");
        Assert.Equal("—",leakageCurrent.DisplayValue);
        Assert.Contains("请重新进行连接测试",viewModel.Notice);
    }

    [Fact]
    public async Task Molded_case_settings_restore_tabs_and_protection_read_route()
    {
        var protectionService=new RecordingProtectionDataService();
        var settingsService=new RecordingSettingsService();
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10",DeviceType:DeviceType.MoldedCaseCircuitBreaker),
            deviceService:new ConnectedDeviceDataService(),
            protectionService:protectionService,
            settingsService:settingsService);

        Assert.True(viewModel.IsMoldedCaseCircuitBreaker);
        Assert.Empty(viewModel.FaultRows);
        Assert.Contains(2400,viewModel.BaudRates);
        Assert.Contains(4800,viewModel.BaudRates);

        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        await viewModel.ReadProtectionCommand.Execute().ToTask();

        Assert.Equal(1,protectionService.Calls);
        Assert.True(viewModel.CanExportProtection);
        Assert.Equal("100 A",viewModel.ProtectionRows.SelectMany(RowItems)
            .Single(item=>item.Name=="长延时电流设定值 Ir1").DisplayValue);

        viewModel.SelectedDevice=viewModel.DeviceOptions.Single(
            option=>option.Value==DeviceType.FrameController);
        await Task.Yield();
        Assert.Equal(DeviceType.FrameController,settingsService.LastSaved?.DeviceType);
    }

    [Fact]
    public async Task Frame_controller_exposes_and_reads_protection_data_tab()
    {
        var protectionService=new RecordingProtectionDataService();
        await using var viewModel=CreateViewModel(
            ["COM10"],new AppSettings(PortName:"COM10",DeviceType:DeviceType.FrameController),
            deviceService:new ConnectedDeviceDataService(),protectionService:protectionService);

        Assert.True(viewModel.IsFrameController);
        Assert.True(viewModel.HasProtectionData);
        Assert.Contains(viewModel.ProtectionRows.SelectMany(RowItems),
            item=>item.Name=="过载动作值" && item.DisplayValue=="— A");

        await viewModel.ToggleSerialCommand.Execute().ToTask();
        await viewModel.TestConnectionCommand.Execute().ToTask();
        Assert.True(viewModel.CanReadProtection);
        await viewModel.ReadProtectionCommand.Execute().ToTask();

        Assert.Equal(DeviceType.FrameController,protectionService.LastDeviceType);
        Assert.Equal("630 A",viewModel.ProtectionRows.SelectMany(RowItems)
            .Single(item=>item.Name=="过载动作值").DisplayValue);
        Assert.True(viewModel.CanExportProtection);
    }

    [Fact]
    public async Task Address_scanner_uses_probe_register_for_selected_device_type()
    {
        var client=new ProbeRecordingClient();
        await using var viewModel=CreateViewModel(
            ["COM10"],
            new AppSettings(PortName:"COM10",DeviceType:DeviceType.MoldedCaseCircuitBreaker),
            client:client);
        await viewModel.ToggleSerialCommand.Execute().ToTask();
        using var scanner=new SlaveAddressScannerViewModel(client,viewModel)
        {
            FromAddress=1,
            ToAddress=1,
        };

        await scanner.ScanCommand.Execute().ToTask();
        viewModel.SelectedDevice=viewModel.DeviceOptions.Single(
            option=>option.Value==DeviceType.FrameController);
        await scanner.ScanCommand.Execute().ToTask();

        Assert.Equal([(ushort)0x0001,(ushort)0x0100],client.ReadStarts);
    }

    private static IEnumerable<DataItemViewModel> RowItems(DataRowViewModel row) =>
        new[] { row.Left, row.Right }.OfType<DataItemViewModel>();

    private static MainViewModel CreateViewModel(
        IReadOnlyList<string> ports,
        AppSettings settings,
        IModbusRtuClient? client=null,
        IProtocolTrace? trace=null,
        IDeviceDataService? deviceService=null,
        IProtectionDataService? protectionService=null,
        IWaveformDataService? waveformService=null,
        IFaultRecordService? faultService=null,
        IWaveformTimeOffsetStore? waveformTimeOffsetStore=null,
        ISettingsService? settingsService=null)
    {
        client??=new FakeClient();
        trace??=new RecordingProtocolTrace();
        return new MainViewModel(
            client,
            new FakePortCatalog(ports),
            deviceService ?? new FakeDeviceDataService(),
            protectionService ?? new FakeProtectionDataService(),
            faultService ?? new FakeFaultRecordService(),
            waveformService ?? new FakeWaveformDataService(),
            waveformTimeOffsetStore ?? new FakeWaveformTimeOffsetStore(),
            settingsService ?? new FakeSettingsService(),
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

    private sealed class ProbeRecordingClient : IModbusRtuClient
    {
        public bool IsOpen { get; private set; }
        public List<ushort> ReadStarts { get; }=[];
        public ValueTask OpenAsync(SerialConnectionOptions options,CancellationToken cancellationToken=default)
        {
            IsOpen=true;
            return ValueTask.CompletedTask;
        }
        public ValueTask CloseAsync(CancellationToken cancellationToken=default)
        {
            IsOpen=false;
            return ValueTask.CompletedTask;
        }
        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress,ushort startAddress,ushort count,
            CancellationToken cancellationToken=default)
        {
            ReadStarts.Add(startAddress);
            return Task.FromResult<ushort[]>([1]);
        }
        public Task WriteSingleRegisterAsync(byte slaveAddress,ushort address,ushort value,
            CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class FakeDeviceDataService : IDeviceDataService
    {
        public Task<bool> TestConnectionAsync(byte slaveAddress,DeviceType deviceType,
            CancellationToken cancellationToken=default)=>
            Task.FromResult(false);

        public Task<DataReadResult> ReadAsync(byte slaveAddress,DeviceType deviceType,WordOrder wordOrder,
            BreakerSeries controllerSeries,CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));
    }

    private sealed class ConnectedDeviceDataService(IReadOnlyList<DecodedValue>? values=null) : IDeviceDataService
    {
        public Task<bool> TestConnectionAsync(byte slaveAddress,DeviceType deviceType,
            CancellationToken cancellationToken=default)=>
            Task.FromResult(true);

        public Task<DataReadResult> ReadAsync(byte slaveAddress,DeviceType deviceType,WordOrder wordOrder,
            BreakerSeries controllerSeries,CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult(values ?? [],[],DateTimeOffset.Now));
    }

    private sealed class RecordingDeviceDataService : IDeviceDataService
    {
        public List<DeviceType> TestedTypes { get; }=[];

        public Task<bool> TestConnectionAsync(byte slaveAddress,DeviceType deviceType,
            CancellationToken cancellationToken=default)
        {
            TestedTypes.Add(deviceType);
            return Task.FromResult(true);
        }

        public Task<DataReadResult> ReadAsync(byte slaveAddress,DeviceType deviceType,WordOrder wordOrder,
            BreakerSeries controllerSeries,CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));
    }

    private sealed class FakeProtectionDataService : IProtectionDataService
    {
        public Task<DataReadResult> ReadAsync(byte slaveAddress,DeviceType deviceType,WordOrder wordOrder,
            BreakerSeries controllerSeries,CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));
    }

    private sealed class RecordingProtectionDataService : IProtectionDataService
    {
        public int Calls { get; private set; }
        public DeviceType? LastDeviceType { get; private set; }

        public Task<DataReadResult> ReadAsync(byte slaveAddress,DeviceType deviceType,WordOrder wordOrder,
            BreakerSeries controllerSeries,CancellationToken cancellationToken=default)
        {
            Calls++;
            LastDeviceType=deviceType;
            var value=new DecodedValue(
                deviceType==DeviceType.FrameController ? "过载动作值" : "长延时电流设定值 Ir1",
                deviceType==DeviceType.FrameController ? [1280] : [0x0016],
                deviceType==DeviceType.FrameController ? "630" : "100","A","×1",[],ParseStatus.Success,null,
                DateTimeOffset.Now);
            return Task.FromResult(new DataReadResult([value],[],DateTimeOffset.Now));
        }
    }

    private sealed class FakeFaultRecordService(Action? timestampRead=null) : IFaultRecordService
    {
        public FaultRecordType? LastTimestampRecordType { get; private set; }
        public byte? LastTimestampRecordIndex { get; private set; }

        public Task<DataReadResult> ReadAsync(byte slaveAddress,FaultRecordType type,byte recordIndex,
            WordOrder wordOrder,BreakerSeries controllerSeries,TimeSpan readyDelay,
            CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));

        public Task<DateTime> ReadTimestampAsync(byte slaveAddress,FaultRecordType type,byte recordIndex,
            TimeSpan readyDelay,CancellationToken cancellationToken=default)
        {
            LastTimestampRecordType=type;
            LastTimestampRecordIndex=recordIndex;
            timestampRead?.Invoke();
            return Task.FromResult(new DateTime(2026,7,22,14,30,1,DateTimeKind.Unspecified));
        }
    }

    private sealed class FakeWaveformTimeOffsetStore(int milliseconds=123,Action? called=null) : IWaveformTimeOffsetStore
    {
        public Task<int> GetOrCreateAsync(DateTime faultRecordTime,CancellationToken cancellationToken=default)
        {
            called?.Invoke();
            return Task.FromResult(milliseconds);
        }
    }

    private sealed class SucceedThenFailTimestampFaultRecordService : IFaultRecordService
    {
        private int _timestampCalls;

        public Task<DataReadResult> ReadAsync(byte slaveAddress,FaultRecordType type,byte recordIndex,
            WordOrder wordOrder,BreakerSeries controllerSeries,TimeSpan readyDelay,
            CancellationToken cancellationToken=default)=>
            Task.FromResult(new DataReadResult([],[],DateTimeOffset.Now));

        public Task<DateTime> ReadTimestampAsync(byte slaveAddress,FaultRecordType type,byte recordIndex,
            TimeSpan readyDelay,CancellationToken cancellationToken=default)=>
            _timestampCalls++==0
                ? Task.FromResult(new DateTime(2026,7,22,14,30,1,DateTimeKind.Unspecified))
                : Task.FromException<DateTime>(new FormatException("768～770 均为 0000H"));
    }

    private sealed class FakeWaveformDataService : IWaveformDataService
    {
        public Task<WaveformData> ReadAsync(byte slaveAddress,
            IProgress<WaveformReadProgress>? progress=null,
            CancellationToken cancellationToken=default)=>
            Task.FromException<WaveformData>(new InvalidOperationException("测试未配置录波数据"));
    }

    private sealed class CompletedWaveformDataService(Action? read=null) : IWaveformDataService
    {
        public Task<WaveformData> ReadAsync(byte slaveAddress,
            IProgress<WaveformReadProgress>? progress=null,
            CancellationToken cancellationToken=default)
        {
            read?.Invoke();
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
                new WaveformPoint(0,0,0,-80,22953,-22953,0,0xAC00,0xAC80,0xAD00),
            ];
            return Task.FromResult(new WaveformData(
                new DateTimeOffset(2026,8,11,12,0,0,TimeSpan.FromHours(8)),
                3200,
                points,
                22953,
                22953,
                0,
                WaveformCalibration.FromRegisterValue(0x0204)));
        }
    }

    private sealed class CountingWaveformDataService : IWaveformDataService
    {
        private readonly CompletedWaveformDataService _inner=new();
        public int Calls { get; private set; }

        public Task<WaveformData> ReadAsync(byte slaveAddress,
            IProgress<WaveformReadProgress>? progress=null,
            CancellationToken cancellationToken=default)
        {
            Calls++;
            return _inner.ReadAsync(slaveAddress,progress,cancellationToken);
        }
    }

    private sealed class SucceedThenFailWaveformDataService : IWaveformDataService
    {
        private int _calls;

        public Task<WaveformData> ReadAsync(byte slaveAddress,
            IProgress<WaveformReadProgress>? progress=null,
            CancellationToken cancellationToken=default)
        {
            if(_calls++>0)
                return Task.FromException<WaveformData>(
                    new InvalidOperationException("框架等级寄存器 1552 读取失败"));

            IReadOnlyList<WaveformPoint> points=
            [
                new WaveformPoint(0,0,0,-80,22953,-22953,0,0xB000,0xB040,0xB080),
            ];
            return Task.FromResult(new WaveformData(
                DateTimeOffset.Now,3200,points,22953,22953,0,
                WaveformCalibration.FromRegisterValue(0x0204)));
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken=default)=>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings,CancellationToken cancellationToken=default)=>
            Task.CompletedTask;
    }

    private sealed class RecordingSettingsService : ISettingsService
    {
        public AppSettings? LastSaved { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken=default)=>
            Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings,CancellationToken cancellationToken=default)
        {
            LastSaved=settings;
            return Task.CompletedTask;
        }
    }
}
