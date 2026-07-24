using System.Collections.ObjectModel;
using Avalonia.Media;
using ReactiveUI;
using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;
using WireLink.Core.Services;

namespace WireLink.App.ViewModels;

/// <summary>主窗口状态机：串口、设备连接、互斥读取、自动刷新和导出入口。</summary>
public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IModbusRtuClient _client;
    private readonly ISerialPortCatalog _ports;
    private readonly IDeviceDataService _deviceService;
    private readonly IFaultRecordService _faultService;
    private readonly ISettingsService _settingsService;
    private readonly IProtocolTrace _trace;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _autoRefreshCancellation;
    private string _portName;
    private int _baudRate;
    private int? _deviceAddress;
    private int _refreshSeconds;
    private int _readTimeoutMilliseconds;
    private int _faultDelayMilliseconds;
    private bool _isSerialOpen;
    private bool _isDeviceConnected;
    private bool _isBusy;
    private bool _autoRefresh;
    private int _consecutiveFailures;
    private bool _showAddressRequired;
    private string _notice = "请选择串口并打开";
    private AppThemeMode _theme;
    private string _controllerName;
    private DateTimeOffset _deviceReadAt;
    private DateTimeOffset _faultReadAt;

    public MainViewModel(IModbusRtuClient client, ISerialPortCatalog ports, IDeviceDataService deviceService,
        IFaultRecordService faultService, ISettingsService settingsService, IProtocolTrace trace, AppSettings settings)
    {
        _client=client; _ports=ports; _deviceService=deviceService; _faultService=faultService;
        _settingsService=settingsService; _trace=trace;
        _portName=settings.PortName; _baudRate=settings.BaudRate; _deviceAddress=settings.DeviceAddress;
        _refreshSeconds=settings.RefreshSeconds; _theme=settings.Theme;
        _controllerName=settings.ControllerSeries == BreakerSeries.BW3 ? "BW3 的控制器" : "BW1 的控制器";
        _readTimeoutMilliseconds=settings.ReadTimeoutMilliseconds; _faultDelayMilliseconds=settings.FaultReadyDelayMilliseconds;
        RefreshPortsCommand=ReactiveCommand.Create(RefreshPorts);
        ToggleSerialCommand=ReactiveCommand.CreateFromTask(ToggleSerialAsync);
        TestConnectionCommand=ReactiveCommand.CreateFromTask(TestConnectionAsync);
        ReadDeviceCommand=ReactiveCommand.CreateFromTask(ReadDeviceAsync);
        ReadFaultCommand=ReactiveCommand.CreateFromTask(ReadFaultAsync);
        ExportDeviceCommand=ReactiveCommand.Create(RequestDeviceExport);
        ExportFaultCommand=ReactiveCommand.Create(RequestFaultExport);
        ShowLogCommand=ReactiveCommand.Create(() => ShowLogRequested?.Invoke(this, EventArgs.Empty));
        RefreshPorts();
        Merge(DeviceRows,CreatePlaceholders(RegisterCatalog.DeviceDefinitions),null);
        Merge(FaultRows,CreatePlaceholders(RegisterCatalog.FaultDefinitions),null);
    }

    public ObservableCollection<string> PortNames { get; }=[];
    public IReadOnlyList<int> BaudRates { get; }=[9600,19200,38400,115200];
    public IReadOnlyList<AppThemeMode> Themes { get; }=Enum.GetValues<AppThemeMode>();
    public IReadOnlyList<string> ControllerOptions { get; }=["BW1 的控制器","BW3 的控制器"];
    public ObservableCollection<DataRowViewModel> DeviceRows { get; }=[];
    public ObservableCollection<DataRowViewModel> FaultRows { get; }=[];
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> RefreshPortsCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ToggleSerialCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> TestConnectionCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ReadDeviceCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ReadFaultCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ExportDeviceCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ExportFaultCommand { get; }
    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ShowLogCommand { get; }
    public event EventHandler<ExportRequest>? ExportRequested;
    public event EventHandler? ShowLogRequested;
    public event EventHandler<AppThemeMode>? ThemeChanged;

    public string PortName { get=>_portName; set=>this.RaiseAndSetIfChanged(ref _portName,value); }
    public int BaudRate { get=>_baudRate; set=>this.RaiseAndSetIfChanged(ref _baudRate,value); }
    public int? DeviceAddress
    {
        get=>_deviceAddress;
        set
        {
            int? address=value is null ? null : Math.Clamp(value.Value,1,255);
            this.RaiseAndSetIfChanged(ref _deviceAddress,address);
            if(address is not null) SetAddressRequired(false);
        }
    }
    public string AddressHint => _showAddressRequired ? "设备地址不能为空" : "Modbus 从机地址（1～255）";
    public IBrush AddressHintBrush => _showAddressRequired ? Brushes.IndianRed : Brushes.Gray;
    public int RefreshSeconds { get=>_refreshSeconds; set=>this.RaiseAndSetIfChanged(ref _refreshSeconds,Math.Clamp(value,1,3600)); }
    public int ReadTimeoutMilliseconds { get=>_readTimeoutMilliseconds; set=>this.RaiseAndSetIfChanged(ref _readTimeoutMilliseconds,Math.Clamp(value,100,10000)); }
    public int FaultDelayMilliseconds { get=>_faultDelayMilliseconds; set=>this.RaiseAndSetIfChanged(ref _faultDelayMilliseconds,Math.Clamp(value,0,2000)); }
    public string ControllerName
    {
        get=>_controllerName;
        set { this.RaiseAndSetIfChanged(ref _controllerName,value); _=SaveSettingsAsync(); }
    }
    private BreakerSeries SelectedControllerSeries =>
        ControllerName == "BW3 的控制器" ? BreakerSeries.BW3 : BreakerSeries.BW1;
    public AppThemeMode Theme { get=>_theme; set { this.RaiseAndSetIfChanged(ref _theme,value); ThemeChanged?.Invoke(this,value); _=SaveSettingsAsync(); } }
    public bool IsSerialOpen { get=>_isSerialOpen; private set { this.RaiseAndSetIfChanged(ref _isSerialOpen,value); RaiseState(); } }
    public bool IsDeviceConnected { get=>_isDeviceConnected; private set { this.RaiseAndSetIfChanged(ref _isDeviceConnected,value); RaiseState(); } }
    public bool IsBusy { get=>_isBusy; private set { this.RaiseAndSetIfChanged(ref _isBusy,value); RaiseState(); } }
    public bool AutoRefresh { get=>_autoRefresh; set { this.RaiseAndSetIfChanged(ref _autoRefresh,value); RestartAutoRefresh(); } }
    public string Notice { get=>_notice; private set=>this.RaiseAndSetIfChanged(ref _notice,value); }
    public string SerialButtonText => IsSerialOpen ? "关闭串口" : "打开串口";
    public string SerialStatusText => IsSerialOpen ? "串口已打开" : "串口未打开";
    public string DeviceStatusText => IsDeviceConnected ? "设备通信正常" : "设备未连接";
    public IBrush SerialStatusBrush => IsSerialOpen ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    public IBrush DeviceStatusBrush => IsDeviceConnected ? Brushes.MediumSeaGreen : Brushes.Gray;
    public bool CanConfigureSerial => !IsSerialOpen && !IsBusy;
    public bool CanTest => IsSerialOpen && !IsBusy;
    public bool CanRead => IsDeviceConnected && !IsBusy;
    public bool CanExportDevice => _deviceReadAt!=default && IsDeviceConnected && !IsBusy;
    public bool CanExportFault => _faultReadAt!=default && IsDeviceConnected && !IsBusy;

    public void RefreshPorts()
    {
        var current=PortName; PortNames.Clear();
        foreach(var port in _ports.GetPortNames()) PortNames.Add(port);
        if(!string.IsNullOrWhiteSpace(current) && !PortNames.Contains(current)) PortNames.Insert(0,current);
    }

    private async Task ToggleSerialAsync()
    {
        if(IsBusy) return;
        if(IsSerialOpen) { await CloseSerialAsync(); return; }
        if(string.IsNullOrWhiteSpace(PortName)) { Notice="请输入或选择串口"; return; }
        await RunBusyAsync(async token =>
        {
            await _client.OpenAsync(new SerialConnectionOptions(PortName,BaudRate,
                TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds),TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds)),token);
            IsSerialOpen=true; IsDeviceConnected=false; Notice=$"已打开 {PortName}"; await SaveSettingsAsync();
        },"打开串口失败");
    }

    private async Task CloseSerialAsync()
    {
        _autoRefreshCancellation?.Cancel(); _operationCancellation?.Cancel(); AutoRefresh=false;
        try { await _client.CloseAsync(); } catch(Exception ex) { _trace.Warning($"关闭串口：{ex.Message}"); }
        IsSerialOpen=false; IsDeviceConnected=false; Notice="串口已关闭";
    }

    private async Task TestConnectionAsync()
    {
        if(!CanTest) return;
        if(DeviceAddress is not int address)
        {
            SetAddressRequired(true);
            Notice="设备地址不能为空";
            return;
        }
        await RunBusyAsync(async token =>
        {
            if(await _deviceService.TestConnectionAsync((byte)address,token))
            { IsDeviceConnected=true; _consecutiveFailures=0; Notice=$"设备 {address} 连接测试成功"; await SaveSettingsAsync(); }
        },"连接测试失败", disconnectOnError:true);
    }

    private async Task ReadDeviceAsync()
    {
        if(!CanRead) return;
        if(DeviceAddress is not int address) return;
        await RunBusyAsync(async token =>
        {
            var result=await _deviceService.ReadAsync(
                (byte)address,WordOrder.HighWordFirst,SelectedControllerSeries,token);
            Merge(DeviceRows,result.Values,result.Errors.Count>0 ? "本区间读取失败，显示上次成功值" : null);
            _deviceReadAt=result.ReadAt;
            if(result.Errors.Count>0)
            {
                _consecutiveFailures++; Notice=$"部分读取失败（连续 {_consecutiveFailures} 次）：{result.Errors[0]}";
                if(_consecutiveFailures>=3) { AutoRefresh=false; IsDeviceConnected=false; Notice="连续读取失败三次，已停止刷新，请重新连接测试"; }
            }
            else { _consecutiveFailures=0; Notice=$"设备数据已更新 {result.ReadAt:HH:mm:ss}"; }
        },"读取设备数据失败",countFailure:true);
    }

    private async Task ReadFaultAsync()
    {
        if(!CanRead) return;
        if(DeviceAddress is not int address) return;
        await RunBusyAsync(async token =>
        {
            var result=await _faultService.ReadAsync((byte)address,FaultRecordType.Fault,0,
                WordOrder.HighWordFirst,SelectedControllerSeries,
                TimeSpan.FromMilliseconds(FaultDelayMilliseconds),token);
            if(result.Errors.Count>0) throw new ModbusProtocolException(result.Errors[0]);
            Merge(FaultRows,result.Values,null); _faultReadAt=result.ReadAt;
            Notice="故障记录 0 已读取";
        },"读取故障记录失败");
    }

    private async Task RunBusyAsync(Func<CancellationToken,Task> action,string prefix,bool disconnectOnError=false,bool countFailure=false)
    {
        IsBusy=true; _operationCancellation=new CancellationTokenSource();
        try { await action(_operationCancellation.Token); }
        catch(OperationCanceledException) { Notice="操作已取消"; }
        catch(Exception ex)
        {
            Notice=$"{prefix}：{Friendly(ex)}"; _trace.Error(Notice,ex);
            if(disconnectOnError) IsDeviceConnected=false;
            if(countFailure && ++_consecutiveFailures>=3) { AutoRefresh=false; IsDeviceConnected=false; }
        }
        finally { _operationCancellation.Dispose(); _operationCancellation=null; IsBusy=false; }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        TimeoutException => "设备响应超时",
        ModbusCrcException => "CRC 校验失败",
        ModbusDeviceException device => $"设备异常 {device.ExceptionCode:X2}H：{device.Message}",
        UnauthorizedAccessException => "串口权限不足或被占用",
        _ => ex.Message,
    };

    private void Merge(ObservableCollection<DataRowViewModel> target,IReadOnlyList<DecodedValue> values,string? staleWarning)
    {
        var existing=target.SelectMany(r=>new[]{r.Left,r.Right}.OfType<DataItemViewModel>()).ToDictionary(x=>x.Name);
        foreach(var value in values)
            if(existing.TryGetValue(value.Name,out var item)) item.Update(value); else existing[value.Name]=new DataItemViewModel(value);
        if(staleWarning is not null)
            foreach(var item in existing.Values.Where(x=>values.All(v=>v.Name!=x.Name))) item.MarkStale(staleWarning);
        target.Clear(); var ordered=existing.Values.ToList();
        for(var i=0;i<ordered.Count;i+=2) target.Add(new DataRowViewModel(ordered[i],i+1<ordered.Count?ordered[i+1]:null));
        RaiseState();
    }

    private void RestartAutoRefresh()
    {
        _autoRefreshCancellation?.Cancel(); _autoRefreshCancellation?.Dispose(); _autoRefreshCancellation=null;
        if(!AutoRefresh || !IsDeviceConnected) return;
        _autoRefreshCancellation=new CancellationTokenSource(); var token=_autoRefreshCancellation.Token;
        _=Task.Run(async()=>
        {
            while(!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(RefreshSeconds),token);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ReadDeviceAsync);
            }
        },token);
    }

    private void RequestDeviceExport() { if(CanExportDevice) ExportRequested?.Invoke(this,new ExportRequest("设备数据",Flatten(DeviceRows),_deviceReadAt)); }
    private void RequestFaultExport() { if(CanExportFault) ExportRequested?.Invoke(this,new ExportRequest("故障数据",Flatten(FaultRows),_faultReadAt,FaultRecordType.Fault,0)); }
    private static IReadOnlyList<DecodedValue> Flatten(IEnumerable<DataRowViewModel> rows)=>rows.SelectMany(r=>new[]{r.Left,r.Right}.OfType<DataItemViewModel>()).Select(x=>x.Value).ToArray();
    private static IReadOnlyList<DecodedValue> CreatePlaceholders(IEnumerable<RegisterDefinition> definitions)=>definitions
        .Select(definition=>new DecodedValue(definition.Name,definition.Addresses,"—",definition.Unit,"尚未读取",[],ParseStatus.ReadFailed,"尚未读取",DateTimeOffset.MinValue))
        .ToArray();

    private Task SaveSettingsAsync()=>_settingsService.SaveAsync(new AppSettings(
        PortName,BaudRate,(byte)(DeviceAddress ?? 1),RefreshSeconds,Theme,WordOrder.HighWordFirst,
        ReadTimeoutMilliseconds,FaultDelayMilliseconds,SelectedControllerSeries));
    private void SetAddressRequired(bool value)
    {
        if(_showAddressRequired==value) return;
        _showAddressRequired=value;
        this.RaisePropertyChanged(nameof(AddressHint));
        this.RaisePropertyChanged(nameof(AddressHintBrush));
    }
    private void RaiseState()
    {
        foreach(var name in new[]{nameof(SerialButtonText),nameof(SerialStatusText),nameof(DeviceStatusText),nameof(SerialStatusBrush),nameof(DeviceStatusBrush),nameof(CanConfigureSerial),nameof(CanTest),nameof(CanRead),nameof(CanExportDevice),nameof(CanExportFault)}) this.RaisePropertyChanged(name);
    }

    public async ValueTask DisposeAsync() { await CloseSerialAsync(); await _client.DisposeAsync(); }
}
