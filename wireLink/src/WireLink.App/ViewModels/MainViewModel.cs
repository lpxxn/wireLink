using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using ReactiveUI;
using SkiaSharp;
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
    private readonly IProtectionDataService _protectionService;
    private readonly IFaultRecordService _faultService;
    private readonly IWaveformDataService _waveformService;
    private readonly IWaveformTimeOffsetStore _waveformTimeOffsetStore;
    private readonly ISettingsService _settingsService;
    private readonly IProtocolTrace _trace;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _autoRefreshCancellation;
    private string _portName;
    private int _baudRate;
    private int? _deviceAddress;
    private int? _refreshSeconds;
    private int? _readTimeoutMilliseconds;
    private int? _faultDelayMilliseconds;
    private bool _isSerialOpen;
    private bool _isDeviceConnected;
    private bool _isBusy;
    private bool _autoRefresh;
    private int _consecutiveFailures;
    private bool _showAddressRequired;
    private string _notice = string.Empty;
    private AppThemeMode _theme;
    private string _controllerName;
    private DeviceTypeOption _selectedDevice;
    private FaultRecordTypeOption _selectedFaultRecordType;
    private int? _faultRecordIndex = 1;
    private DateTimeOffset _deviceReadAt;
    private DateTimeOffset _faultReadAt;
    private DateTimeOffset _protectionReadAt;
    private FaultRecordType _lastReadFaultRecordType;
    private byte _lastReadFaultRecordIndex;
    private WaveformData? _waveformData;
    private string _waveformProgressText = "尚未读取完整录波数据";
    private string _waveformSummary = "采样率、点数和 RMS 将在完整读取后显示";
    private bool _showPhaseA = true;
    private bool _showPhaseB = true;
    private bool _showPhaseC = true;
    private int _selectedDataTabIndex;
    private readonly LineSeries<ObservablePoint> _phaseASeries = CreateWaveformSeries("A 相");
    private readonly LineSeries<ObservablePoint> _phaseBSeries = CreateWaveformSeries("B 相");
    private readonly LineSeries<ObservablePoint> _phaseCSeries = CreateWaveformSeries("C 相");

    public MainViewModel(IModbusRtuClient client, ISerialPortCatalog ports, IDeviceDataService deviceService,
        IProtectionDataService protectionService, IFaultRecordService faultService, IWaveformDataService waveformService,
        IWaveformTimeOffsetStore waveformTimeOffsetStore,
        ISettingsService settingsService, IProtocolTrace trace, AppSettings settings)
    {
        _client = client; _ports = ports; _deviceService = deviceService; _protectionService = protectionService;
        _faultService = faultService;
        _waveformService = waveformService; _waveformTimeOffsetStore = waveformTimeOffsetStore;
        _settingsService = settingsService; _trace = trace;
        _portName = settings.PortName; _baudRate = settings.BaudRate; _deviceAddress = settings.DeviceAddress;
        _refreshSeconds = settings.RefreshSeconds; _theme = settings.Theme;
        _controllerName = settings.ControllerSeries == BreakerSeries.BW3 ? "BW3 的控制器" : "BW1 的控制器";
        _selectedDevice = DeviceOptions.Single(option => option.Value == settings.DeviceType);
        _selectedFaultRecordType = FaultRecordTypes[0];
        _readTimeoutMilliseconds = settings.ReadTimeoutMilliseconds; _faultDelayMilliseconds = settings.FaultReadyDelayMilliseconds;
        RefreshPortsCommand = ReactiveCommand.Create(RefreshPorts);
        ToggleSerialCommand = ReactiveCommand.CreateFromTask(ToggleSerialAsync);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        ReadDeviceCommand = ReactiveCommand.CreateFromTask(ReadDeviceAsync);
        ReadProtectionCommand = ReactiveCommand.CreateFromTask(ReadProtectionAsync);
        ReadFaultCommand = ReactiveCommand.CreateFromTask(ReadFaultAsync);
        ReadWaveformCommand = ReactiveCommand.CreateFromTask(ReadWaveformAsync);
        ExportDeviceCommand = ReactiveCommand.Create(RequestDeviceExport);
        ExportProtectionCommand = ReactiveCommand.Create(RequestProtectionExport);
        ExportFaultCommand = ReactiveCommand.Create(RequestFaultExport);
        ExportWaveformCommand = ReactiveCommand.Create(RequestWaveformExport);
        ShowLogCommand = ReactiveCommand.Create(() => ShowLogRequested?.Invoke(this, EventArgs.Empty));
        RefreshPorts();
        InitializeDeviceRows();
        RefreshWaveformSeries();
    }

    public ObservableCollection<string> PortNames { get; } = [];
    public IReadOnlyList<int> BaudRates { get; } = [2400, 4800, 9600, 19200, 38400, 115200];
    public IReadOnlyList<AppThemeMode> Themes { get; } = Enum.GetValues<AppThemeMode>();
    public IReadOnlyList<string> ControllerOptions { get; } = ["BW1 的控制器", "BW3 的控制器"];
    public IReadOnlyList<DeviceTypeOption> DeviceOptions { get; } =
    [
        new(DeviceType.FrameController, "框架控制器"),
        new(DeviceType.MoldedCaseCircuitBreaker, "塑壳断路器"),
    ];
    public IReadOnlyList<FaultRecordTypeOption> FaultRecordTypes { get; } =
    [
        new(FaultRecordType.Fault,"故障"),
        new(FaultRecordType.Alarm,"报警"),
        new(FaultRecordType.StateChange,"变位"),
    ];
    public ObservableCollection<DataRowViewModel> DeviceRows { get; } = [];
    public ObservableCollection<DataRowViewModel> ProtectionRows { get; } = [];
    public ObservableCollection<DataRowViewModel> FaultRows { get; } = [];
    public ObservableCollection<ISeries> WaveformSeries { get; } = [];
    public Axis[] WaveformXAxes { get; } =
    [
        new Axis
        {
            Name = "录波时间",
            MinLimit = -80,
            MaxLimit = 40,
            MinStep = 20,
            ForceStepToMin = true,
            Labeler = value => value.ToString("0.###"),
            SeparatorsPaint = new SolidColorPaint(new SKColor(148, 163, 184, 150))
            {
                StrokeThickness = 1,
                PathEffect = new DashEffect([5, 5], 0),
            },
        },
    ];
    public Axis[] WaveformYAxes { get; } =
    [
        new Axis { Name = "电流 (A)", Labeler = value => value.ToString("0.0") },
    ];
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshPortsCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleSerialCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> TestConnectionCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReadDeviceCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReadProtectionCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReadFaultCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReadWaveformCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ExportDeviceCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ExportProtectionCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ExportFaultCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ExportWaveformCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ShowLogCommand { get; }
    public event EventHandler<ExportRequest>? ExportRequested;
    public event EventHandler<WaveformExportRequest>? WaveformExportRequested;
    public event EventHandler<ErrorDialogRequest>? ErrorDialogRequested;
    public event EventHandler? ShowLogRequested;
    public event EventHandler<AppThemeMode>? ThemeChanged;

    public string PortName
    {
        get => _portName;
        set
        {
            this.RaiseAndSetIfChanged(ref _portName, value);
            RaiseState();
        }
    }
    public int BaudRate { get => _baudRate; set => this.RaiseAndSetIfChanged(ref _baudRate, value); }
    public int? DeviceAddress
    {
        get => _deviceAddress;
        set
        {
            int? address = value is null ? null : Math.Clamp(value.Value, 1, 255);
            if (_deviceAddress == address) return;
            this.RaiseAndSetIfChanged(ref _deviceAddress, address);
            SetAddressRequired(false);
            if (IsDeviceConnected)
            {
                AutoRefresh = false;
                IsDeviceConnected = false;
            }
            RaiseState();
        }
    }
    public string AddressHint => _showAddressRequired ? "设备地址不能为空" : "Modbus 从机地址（1～255）";
    public IBrush AddressHintBrush => _showAddressRequired ? Brushes.IndianRed : Brushes.Gray;
    public int? RefreshSeconds
    {
        get => _refreshSeconds;
        set
        {
            int? normalized = value is null ? null : Math.Clamp(value.Value, 1, 3600);
            if (_refreshSeconds == normalized) return;
            this.RaiseAndSetIfChanged(ref _refreshSeconds, normalized);
            if (normalized is null && AutoRefresh) AutoRefresh = false;
            RaiseState();
        }
    }
    public int? ReadTimeoutMilliseconds
    {
        get => _readTimeoutMilliseconds;
        set
        {
            int? normalized = value is null ? null : Math.Clamp(value.Value, 100, 10000);
            if (_readTimeoutMilliseconds == normalized) return;
            this.RaiseAndSetIfChanged(ref _readTimeoutMilliseconds, normalized);
            RaiseState();
        }
    }
    public int? FaultDelayMilliseconds
    {
        get => _faultDelayMilliseconds;
        set
        {
            int? normalized = value is null ? null : Math.Clamp(value.Value, 0, 2000);
            if (_faultDelayMilliseconds == normalized) return;
            this.RaiseAndSetIfChanged(ref _faultDelayMilliseconds, normalized);
            RaiseState();
        }
    }
    public int? FaultRecordIndex
    {
        get => _faultRecordIndex;
        set
        {
            int? normalized = value is null ? null : Math.Clamp(value.Value, 0, 15);
            if (_faultRecordIndex == normalized) return;
            this.RaiseAndSetIfChanged(ref _faultRecordIndex, normalized);
            RaiseState();
        }
    }
    public FaultRecordTypeOption SelectedFaultRecordType
    {
        get => _selectedFaultRecordType;
        set => this.RaiseAndSetIfChanged(ref _selectedFaultRecordType, value);
    }
    public string ControllerName
    {
        get => _controllerName;
        set { this.RaiseAndSetIfChanged(ref _controllerName, value); _ = SaveSettingsAsync(); }
    }
    public DeviceTypeOption SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is null || value == _selectedDevice || IsBusy) return;
            this.RaiseAndSetIfChanged(ref _selectedDevice, value);
            HandleDeviceTypeChanged();
            _ = SaveSettingsAsync();
        }
    }
    public DeviceType SelectedDeviceType => SelectedDevice.Value;
    public bool IsFrameController => SelectedDeviceType == DeviceType.FrameController;
    public bool IsMoldedCaseCircuitBreaker => SelectedDeviceType == DeviceType.MoldedCaseCircuitBreaker;
    public bool HasProtectionData => DeviceProfileCatalog.Get(SelectedDeviceType).ProtectionData is not null;
    public bool CanSelectDeviceType => !IsBusy;
    public int SelectedDataTabIndex
    {
        get => _selectedDataTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedDataTabIndex, value);
    }
    private BreakerSeries SelectedControllerSeries =>
        ControllerName == "BW3 的控制器" ? BreakerSeries.BW3 : BreakerSeries.BW1;
    public AppThemeMode Theme { get => _theme; set { this.RaiseAndSetIfChanged(ref _theme, value); ThemeChanged?.Invoke(this, value); _ = SaveSettingsAsync(); } }
    public bool IsSerialOpen { get => _isSerialOpen; private set { this.RaiseAndSetIfChanged(ref _isSerialOpen, value); RaiseState(); } }
    public bool IsDeviceConnected { get => _isDeviceConnected; private set { this.RaiseAndSetIfChanged(ref _isDeviceConnected, value); RaiseState(); } }
    public bool IsBusy { get => _isBusy; private set { this.RaiseAndSetIfChanged(ref _isBusy, value); RaiseState(); } }
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            var enabled = value && CanAutoRefresh;
            this.RaiseAndSetIfChanged(ref _autoRefresh, enabled);
            RestartAutoRefresh();
        }
    }
    public string Notice { get => _notice; private set => this.RaiseAndSetIfChanged(ref _notice, value); }
    public string SerialButtonText => IsSerialOpen ? "关闭串口" : "打开串口";
    public string SerialStatusText => IsSerialOpen ? "串口已打开" : "串口未打开";
    public string DeviceStatusText => IsDeviceConnected ? "设备通信正常" : "设备未连接";
    public IBrush SerialStatusBrush => IsSerialOpen ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    public IBrush DeviceStatusBrush => IsDeviceConnected ? Brushes.MediumSeaGreen : Brushes.Gray;
    public bool CanConfigureSerial => !IsSerialOpen && !IsBusy;
    public bool CanToggleSerial => !IsBusy && (IsSerialOpen ||
        (!string.IsNullOrWhiteSpace(PortName) && ReadTimeoutMilliseconds is not null));
    public bool CanTest => IsSerialOpen && !IsBusy && DeviceAddress is not null;
    public bool CanRead => IsDeviceConnected && !IsBusy && DeviceAddress is not null;
    public bool CanAutoRefresh => CanRead && RefreshSeconds is not null;
    public bool CanReadProtection => CanRead && HasProtectionData;
    public bool CanReadFault => CanRead && IsFrameController && FaultRecordIndex is not null && FaultDelayMilliseconds is not null;
    public bool CanReadWaveform => CanRead && IsFrameController && FaultRecordIndex is not null && FaultDelayMilliseconds is not null;
    public bool CanExportDevice => _deviceReadAt != default && IsDeviceConnected && !IsBusy;
    public bool CanExportProtection => _protectionReadAt != default && IsDeviceConnected && !IsBusy && HasProtectionData;
    public bool CanExportFault => _faultReadAt != default && IsDeviceConnected && !IsBusy && IsFrameController;
    public bool CanExportWaveform => _waveformData is not null && IsDeviceConnected && !IsBusy && IsFrameController;
    public WaveformData? CurrentWaveformData => _waveformData;
    public bool HasWaveformData => _waveformData is not null;
    public bool HasNoWaveformData => _waveformData is null;
    public string WaveformProgressText
    {
        get => _waveformProgressText;
        private set => this.RaiseAndSetIfChanged(ref _waveformProgressText, value);
    }
    public string WaveformSummary
    {
        get => _waveformSummary;
        private set => this.RaiseAndSetIfChanged(ref _waveformSummary, value);
    }
    public bool ShowPhaseA
    {
        get => _showPhaseA;
        set { this.RaiseAndSetIfChanged(ref _showPhaseA, value); RefreshWaveformSeries(); }
    }
    public bool ShowPhaseB
    {
        get => _showPhaseB;
        set { this.RaiseAndSetIfChanged(ref _showPhaseB, value); RefreshWaveformSeries(); }
    }
    public bool ShowPhaseC
    {
        get => _showPhaseC;
        set { this.RaiseAndSetIfChanged(ref _showPhaseC, value); RefreshWaveformSeries(); }
    }

    public void RefreshPorts()
    {
        var current = PortName;
        try
        {
            var availablePorts = _ports.GetPortNames()
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            PortNames.Clear();
            foreach (var port in availablePorts) PortNames.Add(port);

            // 保留手动输入的端口名；只有文本为空时才自动选择扫描到的第一个端口。
            PortName = !string.IsNullOrWhiteSpace(current)
                ? current
                : availablePorts.FirstOrDefault() ?? string.Empty;
            if (availablePorts.Length == 0)
                Notice = "未检测到可用串口，请连接串口设备后重新展开端口列表";
        }
        catch (Exception ex)
        {
            PortNames.Clear();
            PortName = current;
            Notice = $"扫描串口失败：{Friendly(ex)}";
            _trace.Error(Notice, ex);
        }
        RaiseState();
    }

    private async Task ToggleSerialAsync()
    {
        if (IsBusy) return;
        if (IsSerialOpen) { await CloseSerialAsync(); return; }
        if (ReadTimeoutMilliseconds is not int readTimeoutMilliseconds) return;
        if (string.IsNullOrWhiteSpace(PortName))
        {
            Notice = "请输入或选择串口";
            return;
        }
        await RunBusyAsync(async token =>
        {
            await _client.OpenAsync(new SerialConnectionOptions(PortName, BaudRate,
                TimeSpan.FromMilliseconds(readTimeoutMilliseconds), TimeSpan.FromMilliseconds(readTimeoutMilliseconds)), token);
            IsSerialOpen = true; IsDeviceConnected = false; Notice = $"已打开 {PortName}"; await SaveSettingsAsync();
        }, "打开串口失败", showErrorDialog: true);
    }

    private async Task CloseSerialAsync()
    {
        _autoRefreshCancellation?.Cancel(); _operationCancellation?.Cancel(); AutoRefresh = false;
        try { await _client.CloseAsync(); } catch (Exception ex) { _trace.Warning($"关闭串口：{ex.Message}"); }
        IsSerialOpen = false; IsDeviceConnected = false; Notice = "串口已关闭";
    }

    private async Task TestConnectionAsync()
    {
        if (!CanTest) return;
        if (DeviceAddress is not int address)
        {
            SetAddressRequired(true);
            Notice = "设备地址不能为空";
            return;
        }
        await RunBusyAsync(async token =>
        {
            if (await _deviceService.TestConnectionAsync((byte)address, SelectedDeviceType, token))
            {
                IsDeviceConnected = true; _consecutiveFailures = 0;
                Notice = $"设备 {address} 连接测试成功"; await SaveSettingsAsync();
            }
            else
            {
                IsDeviceConnected = false;
                Notice = $"设备 {address} 连接测试失败：无有效响应";
            }
        }, "连接测试失败", disconnectOnError: true);
    }

    private async Task ReadDeviceAsync()
    {
        if (!CanRead) return;
        if (DeviceAddress is not int address) return;
        await RunBusyAsync(async token =>
        {
            var result = await _deviceService.ReadAsync(
                (byte)address, SelectedDeviceType, WordOrder.HighWordFirst, SelectedControllerSeries, token);
            var definitions = DeviceProfileCatalog.Get(SelectedDeviceType).DeviceData.Definitions;
            Merge(
                DeviceRows,
                ForTable(result.Values, definitions),
                result.Errors.Count > 0 ? "本区间读取失败，显示上次成功值" : null);
            _deviceReadAt = result.ReadAt;
            if (result.Errors.Count > 0)
            {
                _consecutiveFailures++; Notice = $"部分读取失败（连续 {_consecutiveFailures} 次）：{result.Errors[0]}";
                if (_consecutiveFailures >= 3) { AutoRefresh = false; IsDeviceConnected = false; Notice = "连续读取失败三次，已停止刷新，请重新连接测试"; }
            }
            else { _consecutiveFailures = 0; Notice = $"设备数据已更新 {result.ReadAt:HH:mm:ss}"; }
        }, "读取设备数据失败", countFailure: true);
    }

    private async Task ReadProtectionAsync()
    {
        if (!CanReadProtection || DeviceAddress is not int address) return;
        await RunBusyAsync(async token =>
        {
            var profile = DeviceProfileCatalog.Get(SelectedDeviceType);
            var definitions = profile.ProtectionData?.Definitions
                ?? throw new InvalidOperationException($"{profile.DisplayName}保护数据目录未配置。");
            var result = await _protectionService.ReadAsync(
                (byte)address, SelectedDeviceType, WordOrder.HighWordFirst, SelectedControllerSeries, token);
            Merge(
                ProtectionRows,
                ForTable(result.Values, definitions),
                result.Errors.Count > 0 ? "本区间读取失败，显示上次成功值" : null);
            _protectionReadAt = result.ReadAt;
            Notice = result.Errors.Count == 0
                ? $"保护数据已更新 {result.ReadAt:HH:mm:ss}"
                : $"保护数据部分读取失败：{result.Errors[0]}";
        }, "读取保护数据失败");
    }

    private async Task ReadFaultAsync()
    {
        if (!CanReadFault || DeviceAddress is not int address ||
           FaultRecordIndex is not int faultRecordIndex || FaultDelayMilliseconds is not int faultDelayMilliseconds) return;
        await RunBusyAsync(async token =>
        {
            var selectedType = SelectedFaultRecordType.Value;
            var selectedIndex = (byte)faultRecordIndex;
            var result = await _faultService.ReadAsync((byte)address, selectedType, selectedIndex,
                WordOrder.HighWordFirst, SelectedControllerSeries,
                TimeSpan.FromMilliseconds(faultDelayMilliseconds), token);
            if (!result.HasData)
                throw new ModbusProtocolException(result.Errors.FirstOrDefault() ?? "没有读取到故障数据");

            Merge(
                FaultRows,
                ForTable(result.Values, RegisterCatalog.FaultDefinitions),
                result.Errors.Count > 0 ? "本字段读取失败，显示上次成功值" : null);
            _faultReadAt = result.ReadAt;
            _lastReadFaultRecordType = selectedType;
            _lastReadFaultRecordIndex = selectedIndex;
            Notice = result.Errors.Count == 0
                ? $"{DescribeFaultRecordType(selectedType)}第 {selectedIndex} 条记录已读取"
                : $"故障数据部分读取失败：{result.Errors[0]}";
        }, "读取故障记录失败");
    }

    private async Task ReadWaveformAsync()
    {
        if (!CanReadWaveform || DeviceAddress is not int address ||
            FaultRecordIndex is not int faultRecordIndex || FaultDelayMilliseconds is not int faultDelayMilliseconds)
            return;
        await RunBusyAsync(async token =>
        {
            var selectedType = SelectedFaultRecordType.Value;
            var selectedIndex = (byte)faultRecordIndex;
            WaveformProgressText =
                $"正在读取故障记录时间：{DescribeFaultRecordType(selectedType)}，第 {selectedIndex} 条记录";

            DateTime faultRecordTime;
            try
            {
                faultRecordTime = await _faultService.ReadTimestampAsync(
                    (byte)address,
                    selectedType,
                    selectedIndex,
                    TimeSpan.FromMilliseconds(faultDelayMilliseconds),
                    token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = $"未读取到有效的故障记录时间，无法读取录波数据：{Friendly(ex)}";
                WaveformProgressText = message;
                ErrorDialogRequested?.Invoke(this, new ErrorDialogRequest("无法读取录波数据", message));
                throw new InvalidOperationException(message, ex);
            }

            var softwareMilliseconds = await _waveformTimeOffsetStore.GetOrCreateAsync(faultRecordTime, token);
            var timing = new WaveformTiming(
                faultRecordTime,
                softwareMilliseconds,
                selectedType,
                selectedIndex);

            WaveformProgressText = "正在读取框架等级：1552 (0610H)";
            var progressStateLock = new object();
            var progressCompleted = false;
            var progress = new Progress<WaveformReadProgress>(value =>
            {
                lock (progressStateLock)
                {
                    // Progress<T> 会异步投递到 UI 队列。读取完成后，队列中可能仍留有最后一次 18/18 回调；
                    // 完成标志一旦置位，就忽略这些迟到的进度，避免“读取完毕”被重新覆盖为“正在读取”。
                    if (progressCompleted) return;
                    var block = value.CurrentBlock;
                    WaveformProgressText = $"正在读取 {value.CompletedBlocks}/{value.TotalBlocks}：{block.Phase} 相，{block.TimeRangeText}，地址 {block.StartAddress:X4}H";
                }
            });
            var data = (await _waveformService.ReadAsync((byte)address, progress, token)) with
            {
                Timing = timing,
            };

            // 只有 1552 标定参数和 18 个录波块全部成功后，才替换上一次完整结果。
            _waveformData = data;
            this.RaisePropertyChanged(nameof(CurrentWaveformData));
            _phaseASeries.Values = data.Points.Select(point => new ObservablePoint(
                point.TimeMilliseconds, data.Calibration.ConvertToAmperes(point.PhaseA))).ToArray();
            _phaseBSeries.Values = data.Points.Select(point => new ObservablePoint(
                point.TimeMilliseconds, data.Calibration.ConvertToAmperes(point.PhaseB))).ToArray();
            _phaseCSeries.Values = data.Points.Select(point => new ObservablePoint(
                point.TimeMilliseconds, data.Calibration.ConvertToAmperes(point.PhaseC))).ToArray();
            ApplyWaveformTimeAxis(data);
            FixWaveformYAxis(data);
            WaveformSummary =
                $"{data.SampleRateHz:0.###} Hz · 每相 {data.Points.Count} 点 · " +
                $"A/B/C RMS：{data.PhaseAAmperesRms:0.0} / {data.PhaseBAmperesRms:0.0} / {data.PhaseCAmperesRms:0.0} A · " +
            // $"{data.Calibration.FrameName}，Rate={data.Calibration.Rate:0.###} · " +
            // $"{DescribeFaultRecordType(timing.RecordType)}第 {timing.RecordIndex} 条 · " +
            // $"故障时间 {FaultRecordTimeDecoder.Format(timing.FaultRecordTime)} + 软件补充 {timing.SoftwareMilliseconds} ms（非设备实测） · " +
            //  $"录波 {timing.WaveformStartTime:yyyy-MM-dd HH:mm:ss.fff}～{data.WaveformEndTime:HH:mm:ss.fffffff}";
              $"录波 {timing.WaveformStartTime:yyyy-MM-dd HH:mm:ss.fff}";
            lock (progressStateLock)
            {
                progressCompleted = true;
                WaveformProgressText = $"完整录波读取完成 {data.ReadAt:yyyy-MM-dd HH:mm:ss}";
            }
            Notice = $"录波数据已更新：每相 {data.Points.Count} 点";
            this.RaisePropertyChanged(nameof(HasWaveformData));
            this.RaisePropertyChanged(nameof(HasNoWaveformData));
            RaiseState();
        }, "读取录波数据失败");
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action, string prefix, bool disconnectOnError = false,
        bool countFailure = false, bool showErrorDialog = false)
    {
        IsBusy = true; _operationCancellation = new CancellationTokenSource();
        try { await action(_operationCancellation.Token); }
        catch (OperationCanceledException) { Notice = "操作已取消"; }
        catch (Exception ex)
        {
            var message = Friendly(ex);
            Notice = $"{prefix}：{message}"; _trace.Error(Notice, ex);
            if (showErrorDialog)
                ErrorDialogRequested?.Invoke(this, new ErrorDialogRequest(prefix, message));
            if (disconnectOnError) IsDeviceConnected = false;
            if (countFailure && ++_consecutiveFailures >= 3) { AutoRefresh = false; IsDeviceConnected = false; }
        }
        finally { _operationCancellation.Dispose(); _operationCancellation = null; IsBusy = false; }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        TimeoutException => "设备响应超时",
        ModbusCrcException => "CRC 校验失败",
        ModbusDeviceException device => $"设备异常 {device.ExceptionCode:X2}H：{device.Message}",
        UnauthorizedAccessException => "串口权限不足或被占用",
        IOException io => $"串口不存在、已断开或通信异常：{io.Message}",
        ArgumentException argument => $"串口参数无效：{argument.Message}",
        _ => ex.Message,
    };

    private void Merge(ObservableCollection<DataRowViewModel> target, IReadOnlyList<DecodedValue> values, string? staleWarning)
    {
        var existing = target.SelectMany(r => new[] { r.Left, r.Right }.OfType<DataItemViewModel>()).ToDictionary(x => x.Name);
        foreach (var value in values)
            if (existing.TryGetValue(value.Name, out var item)) item.Update(value); else existing[value.Name] = new DataItemViewModel(value);
        if (staleWarning is not null)
            foreach (var item in existing.Values.Where(x => values.All(v => v.Name != x.Name))) item.MarkStale(staleWarning);
        target.Clear(); var ordered = existing.Values.ToList();
        for (var i = 0; i < ordered.Count; i += 2) target.Add(new DataRowViewModel(ordered[i], i + 1 < ordered.Count ? ordered[i + 1] : null));
        RaiseState();
    }

    private void HandleDeviceTypeChanged()
    {
        AutoRefresh = false;
        IsDeviceConnected = false;
        _consecutiveFailures = 0;
        _deviceReadAt = default;
        _protectionReadAt = default;
        _faultReadAt = default;
        _waveformData = null;
        _phaseASeries.Values = Array.Empty<ObservablePoint>();
        _phaseBSeries.Values = Array.Empty<ObservablePoint>();
        _phaseCSeries.Values = Array.Empty<ObservablePoint>();
        WaveformProgressText = "尚未读取完整录波数据";
        WaveformSummary = "采样率、点数和 RMS 将在完整读取后显示";
        InitializeDeviceRows();
        SelectedDataTabIndex = 0;
        this.RaisePropertyChanged(nameof(SelectedDeviceType));
        this.RaisePropertyChanged(nameof(IsFrameController));
        this.RaisePropertyChanged(nameof(IsMoldedCaseCircuitBreaker));
        this.RaisePropertyChanged(nameof(HasProtectionData));
        this.RaisePropertyChanged(nameof(CurrentWaveformData));
        this.RaisePropertyChanged(nameof(HasWaveformData));
        this.RaisePropertyChanged(nameof(HasNoWaveformData));
        Notice = $"已切换为{SelectedDevice.DisplayName}，串口保持当前状态，请重新进行连接测试";
        RaiseState();
    }

    private void InitializeDeviceRows()
    {
        DeviceRows.Clear();
        ProtectionRows.Clear();
        FaultRows.Clear();

        var profile = DeviceProfileCatalog.Get(SelectedDeviceType);
        Merge(DeviceRows, CreatePlaceholders(profile.DeviceData.Definitions), null);
        if (profile.ProtectionData is not null)
            Merge(ProtectionRows, CreatePlaceholders(profile.ProtectionData.Definitions), null);
        if (IsFrameController)
            Merge(FaultRows, CreatePlaceholders(RegisterCatalog.FaultDefinitions), null);
    }

    private void RestartAutoRefresh()
    {
        _autoRefreshCancellation?.Cancel(); _autoRefreshCancellation?.Dispose(); _autoRefreshCancellation = null;
        if (!AutoRefresh || !CanAutoRefresh || RefreshSeconds is not int refreshSeconds) return;
        _autoRefreshCancellation = new CancellationTokenSource(); var token = _autoRefreshCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(refreshSeconds), token);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(ReadDeviceAsync);
            }
        }, token);
    }

    private void RequestDeviceExport()
    {
        if (CanExportDevice)
            ExportRequested?.Invoke(this, new ExportRequest(
                IsFrameController ? "设备数据" : "塑壳断路器设备数据",
                Flatten(DeviceRows), _deviceReadAt));
    }
    private void RequestProtectionExport()
    {
        if (CanExportProtection)
            ExportRequested?.Invoke(this, new ExportRequest(
                $"{SelectedDevice.DisplayName}保护数据", Flatten(ProtectionRows), _protectionReadAt));
    }
    private void RequestFaultExport()
    {
        if (CanExportFault)
            ExportRequested?.Invoke(this, new ExportRequest(
                "故障数据", Flatten(FaultRows), _faultReadAt, _lastReadFaultRecordType, _lastReadFaultRecordIndex));
    }
    private void RequestWaveformExport()
    {
        if (CanExportWaveform && _waveformData is not null)
            WaveformExportRequested?.Invoke(this, new WaveformExportRequest("录波数据", _waveformData));
    }
    private static string DescribeFaultRecordType(FaultRecordType type) => type switch
    {
        FaultRecordType.Fault => "故障",
        FaultRecordType.Alarm => "报警",
        FaultRecordType.StateChange => "变位",
        _ => type.ToString(),
    };
    private static IReadOnlyList<DecodedValue> Flatten(IEnumerable<DataRowViewModel> rows) => rows.SelectMany(r => new[] { r.Left, r.Right }.OfType<DataItemViewModel>()).Select(x => x.Value).ToArray();
    private static IReadOnlyList<DecodedValue> CreatePlaceholders(IEnumerable<RegisterDefinition> definitions) => definitions
        .Where(definition => definition.ShowInTable)
        .Select(definition => definition.IsReadable
            ? new DecodedValue(definition.Name, definition.Addresses, "—", definition.Unit, "尚未读取", [], ParseStatus.ReadFailed, "尚未读取", DateTimeOffset.MinValue)
            : new DecodedValue(definition.Name, definition.Addresses, definition.FixedValue ?? string.Empty,
                definition.Unit, definition.FormatDescription, [], ParseStatus.Success, null, DateTimeOffset.MinValue))
        .ToArray();
    private static IReadOnlyList<DecodedValue> ForTable(
        IReadOnlyList<DecodedValue> values,
        IEnumerable<RegisterDefinition> definitions)
    {
        var hidden = definitions
            .Where(definition => !definition.ShowInTable)
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        return hidden.Count == 0 ? values : values.Where(value => !hidden.Contains(value.Name)).ToArray();
    }

    private Task SaveSettingsAsync()
    {
        if (DeviceAddress is not int deviceAddress || RefreshSeconds is not int refreshSeconds ||
           ReadTimeoutMilliseconds is not int readTimeoutMilliseconds ||
           FaultDelayMilliseconds is not int faultDelayMilliseconds)
            return Task.CompletedTask;

        return _settingsService.SaveAsync(new AppSettings(
            PortName, BaudRate, (byte)deviceAddress, refreshSeconds, Theme, WordOrder.HighWordFirst,
            readTimeoutMilliseconds, faultDelayMilliseconds, SelectedControllerSeries, SelectedDeviceType));
    }
    private void SetAddressRequired(bool value)
    {
        if (_showAddressRequired == value) return;
        _showAddressRequired = value;
        this.RaisePropertyChanged(nameof(AddressHint));
        this.RaisePropertyChanged(nameof(AddressHintBrush));
    }
    private void RaiseState()
    {
        foreach (var name in new[] { nameof(SerialButtonText), nameof(SerialStatusText), nameof(DeviceStatusText), nameof(SerialStatusBrush), nameof(DeviceStatusBrush), nameof(CanConfigureSerial), nameof(CanToggleSerial), nameof(CanTest), nameof(CanRead), nameof(CanAutoRefresh), nameof(CanSelectDeviceType), nameof(CanReadProtection), nameof(CanReadFault), nameof(CanReadWaveform), nameof(CanExportDevice), nameof(CanExportProtection), nameof(CanExportFault), nameof(CanExportWaveform) }) this.RaisePropertyChanged(name);
    }

    private static LineSeries<ObservablePoint> CreateWaveformSeries(string name) => new()
    {
        Name = name,
        Values = Array.Empty<ObservablePoint>(),
        Fill = null,
        GeometrySize = 0,
        LineSmoothness = 0,
        XToolTipLabelFormatter = point => $"{point.Coordinate.SecondaryValue:0.####} ms",
        YToolTipLabelFormatter = point => $"{point.Coordinate.PrimaryValue:0.0} A",
    };

    /// <summary>
    /// 曲线仍使用 -80～40 的小数坐标，标签和工具提示才转换为具体时间。
    /// 这样既保留 0.3125 ms 精度，也避免使用巨大时间戳造成浮点精度损失。
    /// </summary>
    private void ApplyWaveformTimeAxis(WaveformData data)
    {
        var axis = WaveformXAxes[0];
        axis.Name = "录波时间";
        axis.Labeler = value => data.GetAbsoluteTime(value)
            .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        foreach (var series in new[] { _phaseASeries, _phaseBSeries, _phaseCSeries })
            series.XToolTipLabelFormatter = point => data.GetAbsoluteTime(point.Coordinate.SecondaryValue)
                .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        this.RaisePropertyChanged(nameof(WaveformXAxes));
    }

    /// <summary>
    /// 使用本次完整录波的三相全部安培值固定 Y 轴。相别显隐只改变曲线集合，不再触发自动缩放。
    /// </summary>
    private void FixWaveformYAxis(WaveformData data)
    {
        var axis = WaveformYAxes[0];
        if (data.Points.Count == 0)
        {
            axis.MinLimit = null;
            axis.MaxLimit = null;
            return;
        }

        var maximumAbsoluteValue = data.Points.Max(point => Math.Max(
            Math.Abs(data.Calibration.ConvertToAmperes(point.PhaseA)),
            Math.Max(
                Math.Abs(data.Calibration.ConvertToAmperes(point.PhaseB)),
                Math.Abs(data.Calibration.ConvertToAmperes(point.PhaseC)))));
        var padding = Math.Max(1.0, maximumAbsoluteValue * 0.08);
        var limit = maximumAbsoluteValue + padding;
        axis.MinLimit = -limit;
        axis.MaxLimit = limit;
    }

    private void RefreshWaveformSeries()
    {
        WaveformSeries.Clear();
        if (ShowPhaseA) WaveformSeries.Add(_phaseASeries);
        if (ShowPhaseB) WaveformSeries.Add(_phaseBSeries);
        if (ShowPhaseC) WaveformSeries.Add(_phaseCSeries);
    }

    public async ValueTask DisposeAsync() { await CloseSerialAsync(); await _client.DisposeAsync(); }
}
