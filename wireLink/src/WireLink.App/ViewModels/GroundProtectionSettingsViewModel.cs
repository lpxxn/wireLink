using System.ComponentModel;
using ReactiveUI;
using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.App.ViewModels;

public sealed record GroundProtectionModeOption(byte Value, string Name, string Description)
{
    public string DisplayText => $"{Value} · {Name}";
}

/// <summary>读取并修改框架控制器 1793.bit12～bit10 接地保护方式。</summary>
public sealed class GroundProtectionSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IModbusRtuClient _client;
    private readonly MainViewModel _mainViewModel;
    private bool _isBusy;
    private bool _hasReadValue;
    private ushort _rawValue;
    private GroundProtectionModeOption? _selectedMode;
    private string _status = "正在读取寄存器 1793…";

    public GroundProtectionSettingsViewModel(IModbusRtuClient client, MainViewModel mainViewModel)
    {
        _client = client;
        _mainViewModel = mainViewModel;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        ReadCommand = ReactiveCommand.CreateFromTask(ReadAsync);
        WriteCommand = ReactiveCommand.CreateFromTask(WriteAsync);
        UpdateAvailabilityStatus();
    }

    public static IReadOnlyList<GroundProtectionModeOption> Modes { get; } =
    [
        new(0, "关闭", "关闭接地保护；模式相关保护参数保留原始值。"),
        new(1, "漏电型", "保护动作值按 ×0.01 A，动作时间按漏电时间表解析。"),
        new(2, "差值型", "协议尚未确认差值型参数换算；保护数据页保留原始值并提示。"),
        new(3, "地电流型", "保护动作值按电流变比换算，动作时间按 ×0.01 s。"),
    ];

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ReadCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> WriteCommand { get; }

    public GroundProtectionModeOption? SelectedMode
    {
        get => _selectedMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMode, value);
            this.RaisePropertyChanged(nameof(SelectedModeDescription));
            this.RaisePropertyChanged(nameof(CanWrite));
        }
    }

    public string SelectedModeDescription => SelectedMode?.Description ?? "请选择要写入的接地保护方式。";
    public string RawValueText => HasReadValue ? $"0x{_rawValue:X4}（{_rawValue}）" : "—";
    public string CurrentModeText
    {
        get
        {
            if (!HasReadValue) return "—";
            var mode = ExtractMode(_rawValue);
            return Modes.FirstOrDefault(item => item.Value == mode)?.DisplayText ?? $"{mode} · 保留值";
        }
    }

    public string ConnectionText => !_mainViewModel.IsFrameController
        ? "仅框架控制器支持此设置"
        : _mainViewModel.IsDeviceConnected
            ? $"框架控制器 {_mainViewModel.DeviceAddress} 已连接"
            : "请先在主界面完成框架控制器连接测试";

    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            RaiseAvailability();
        }
    }

    public bool HasReadValue
    {
        get => _hasReadValue;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasReadValue, value);
            this.RaisePropertyChanged(nameof(RawValueText));
            this.RaisePropertyChanged(nameof(CurrentModeText));
            RaiseAvailability();
        }
    }

    public bool CanRead => _mainViewModel.IsFrameController
        && _mainViewModel.IsDeviceConnected
        && !_mainViewModel.IsBusy
        && _mainViewModel.DeviceAddress is not null
        && !IsBusy;
    public bool CanSelectMode => CanRead && HasReadValue;
    public bool CanWrite => CanSelectMode && SelectedMode is not null;

    public Task InitializeAsync() => ReadAsync();

    private async Task ReadAsync()
    {
        if (!CanRead || _mainViewModel.DeviceAddress is not int address)
        {
            UpdateAvailabilityStatus();
            return;
        }

        IsBusy = true;
        HasReadValue = false;
        Status = "正在读取寄存器 1793…";
        try
        {
            var raw = await ReadRegisterAsync((byte)address);
            ApplyRawValue(raw);
            Status = $"读取成功：接地保护方式为 {CurrentModeText}";
        }
        catch (TimeoutException)
        {
            HasReadValue = false;
            Status = "读取失败：设备响应超时";
        }
        catch (Exception ex)
        {
            HasReadValue = false;
            Status = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task WriteAsync()
    {
        if (!CanWrite || SelectedMode is not { } selected || _mainViewModel.DeviceAddress is not int address)
        {
            UpdateAvailabilityStatus();
            return;
        }

        IsBusy = true;
        Status = "正在读取最新 1793 并写入…";
        try
        {
            var latest = await ReadRegisterAsync((byte)address);
            var updated = ReplaceMode(latest, selected.Value);
            await _client.WriteSingleRegisterAsync(
                (byte)address, RegisterCatalog.GroundProtectionModeRegisterAddress, updated);

            var readBack = await ReadRegisterAsync((byte)address);
            if (readBack != updated)
                throw new InvalidDataException($"写入后读回 0x{readBack:X4}，期望 0x{updated:X4}。");

            ApplyRawValue(readBack);
            Status = $"修改成功：接地保护方式已设为 {CurrentModeText}；1793=0x{readBack:X4}";
        }
        catch (TimeoutException)
        {
            HasReadValue = false;
            Status = "修改失败：设备响应超时";
        }
        catch (Exception ex)
        {
            HasReadValue = false;
            Status = $"修改失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<ushort> ReadRegisterAsync(byte address)
    {
        var values = await _client.ReadHoldingRegistersAsync(
            address, RegisterCatalog.GroundProtectionModeRegisterAddress, 1);
        return values.Length == 1
            ? values[0]
            : throw new InvalidDataException($"读取 1793 应返回 1 个寄存器，实际返回 {values.Length} 个。");
    }

    private void ApplyRawValue(ushort raw)
    {
        _rawValue = raw;
        HasReadValue = true;
        SelectedMode = Modes.FirstOrDefault(item => item.Value == ExtractMode(raw));
        this.RaisePropertyChanged(nameof(RawValueText));
        this.RaisePropertyChanged(nameof(CurrentModeText));
    }

    internal static byte ExtractMode(ushort raw) =>
        (byte)((raw & RegisterCatalog.GroundProtectionModeMask) >> 10);

    internal static ushort ReplaceMode(ushort raw, byte mode)
    {
        if (mode > 3)
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "接地保护方式只允许 0～3。");
        return (ushort)((raw & ~RegisterCatalog.GroundProtectionModeMask) | (mode << 10));
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected)
            or nameof(MainViewModel.DeviceAddress)
            or nameof(MainViewModel.SelectedDeviceType)
            or nameof(MainViewModel.IsBusy))
        {
            if (!CanRead)
                HasReadValue = false;
            this.RaisePropertyChanged(nameof(ConnectionText));
            RaiseAvailability();
            UpdateAvailabilityStatus();
        }
    }

    private void UpdateAvailabilityStatus()
    {
        if (!_mainViewModel.IsFrameController)
            Status = "仅框架控制器支持修改接地保护方式。";
        else if (!_mainViewModel.IsDeviceConnected)
            Status = "请先在主界面打开串口并完成连接测试。";
    }

    private void RaiseAvailability()
    {
        this.RaisePropertyChanged(nameof(CanRead));
        this.RaisePropertyChanged(nameof(CanSelectMode));
        this.RaisePropertyChanged(nameof(CanWrite));
    }

    public void Dispose() => _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
}
