using System.Collections.ObjectModel;
using System.ComponentModel;
using ReactiveUI;
using WireLink.Core.Communication;

namespace WireLink.App.ViewModels;

/// <summary>扫描总线上可通信的 Modbus 从机地址。</summary>
public sealed class SlaveAddressScannerViewModel : ViewModelBase, IDisposable
{
    /// <summary>探测用寄存器，与主界面连接测试一致。</summary>
    private const ushort ProbeRegister = 256;

    /// <summary>单地址探测超时；比常规读超时更短，以便快速跳过无响应地址。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    private readonly IModbusRtuClient _client;
    private readonly MainViewModel _mainViewModel;
    private CancellationTokenSource? _scanCancellation;
    private int? _fromAddress = 1;
    private int? _toAddress = 10;
    private bool _isScanning;
    private string _status = "请先打开串口，再扫描从机地址";
    private string _progressText = "";

    public SlaveAddressScannerViewModel(IModbusRtuClient client, MainViewModel mainViewModel)
    {
        _client = client;
        _mainViewModel = mainViewModel;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync);
        StopCommand = ReactiveCommand.Create(StopScan);
        UpdateConnectionStatus();
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ScanCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> StopCommand { get; }
    public ObservableCollection<string> FoundAddresses { get; } = [];

    public int? FromAddress
    {
        get => _fromAddress;
        set
        {
            int? normalized = value is null ? null : Math.Clamp(value.Value, 1, 247);
            this.RaiseAndSetIfChanged(ref _fromAddress, normalized);
            this.RaisePropertyChanged(nameof(CanScan));
        }
    }

    public int? ToAddress
    {
        get => _toAddress;
        set
        {
            int? normalized = value is null ? null : Math.Clamp(value.Value, 1, 247);
            this.RaiseAndSetIfChanged(ref _toAddress, normalized);
            this.RaisePropertyChanged(nameof(CanScan));
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isScanning, value);
            this.RaisePropertyChanged(nameof(CanScan));
            this.RaisePropertyChanged(nameof(CanStop));
        }
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    public string ConnectionText => _mainViewModel.IsSerialOpen
        ? $"串口已打开（{_mainViewModel.PortName}）"
        : "串口未打开，无法扫描";

    public bool CanScan => _mainViewModel.IsSerialOpen
        && !IsScanning
        && FromAddress is not null
        && ToAddress is not null
        && FromAddress.Value <= ToAddress.Value;

    public bool CanStop => IsScanning;

    private async Task ScanAsync()
    {
        if (!CanScan || FromAddress is not int from || ToAddress is not int to)
        {
            Status = _mainViewModel.IsSerialOpen ? "请设置有效的地址范围" : "请先打开串口";
            return;
        }

        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;

        IsScanning = true;
        FoundAddresses.Clear();
        ProgressText = "";
        Status = $"正在扫描地址 {from}～{to}…";

        var found = 0;
        try
        {
            for (var address = from; address <= to; address++)
            {
                token.ThrowIfCancellationRequested();
                ProgressText = $"正在探测地址 {address} / {to}";

                if (await TryProbeAsync((byte)address, token))
                {
                    found++;
                    FoundAddresses.Add($"地址 {address}（0x{address:X2}）可通信");
                }
            }

            Status = found == 0
                ? $"扫描完成：未发现可通信从机（范围 {from}～{to}）"
                : $"扫描完成：发现 {found} 个可通信从机";
            ProgressText = "";
        }
        catch (OperationCanceledException)
        {
            Status = found == 0
                ? "扫描已停止"
                : $"扫描已停止：已发现 {found} 个可通信从机";
            ProgressText = "";
        }
        catch (Exception ex)
        {
            Status = $"扫描失败：{ex.Message}";
            ProgressText = "";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task<bool> TryProbeAsync(byte slaveAddress, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ProbeTimeout);
        try
        {
            var values = await _client.ReadHoldingRegistersAsync(
                slaveAddress, ProbeRegister, 1, probeCts.Token);
            return values.Length == 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 超时、无响应、协议错误均视为该地址不可通信。
            return false;
        }
    }

    private void StopScan()
    {
        if (!IsScanning) return;
        _scanCancellation?.Cancel();
        Status = "正在停止扫描…";
    }

    private void UpdateConnectionStatus()
    {
        this.RaisePropertyChanged(nameof(CanScan));
        this.RaisePropertyChanged(nameof(ConnectionText));
        if (!_mainViewModel.IsSerialOpen)
        {
            if (IsScanning)
                StopScan();
            Status = "请先打开串口，再扫描从机地址";
        }
        else if (!IsScanning)
        {
            Status = "点击“开始扫描”探测可通信从机地址";
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsSerialOpen) or nameof(MainViewModel.PortName))
            UpdateConnectionStatus();
    }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
    }
}
