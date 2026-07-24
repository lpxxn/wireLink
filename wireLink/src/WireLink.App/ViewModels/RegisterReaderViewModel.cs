using System.ComponentModel;
using ReactiveUI;
using WireLink.Core.Communication;

namespace WireLink.App.ViewModels;

/// <summary>供现场诊断使用的单寄存器读取工具。</summary>
public sealed class RegisterReaderViewModel : ViewModelBase, IDisposable
{
    private readonly IModbusRtuClient _client;
    private readonly MainViewModel _mainViewModel;
    private int _registerAddress;
    private bool _isBusy;
    private string _rawValue="—";
    private string _decimalValue="—";
    private string _status="输入寄存器地址后读取";

    public RegisterReaderViewModel(IModbusRtuClient client,MainViewModel mainViewModel)
    {
        _client=client;
        _mainViewModel=mainViewModel;
        _mainViewModel.PropertyChanged+=OnMainViewModelPropertyChanged;
        ReadCommand=ReactiveCommand.CreateFromTask(ReadAsync);
    }

    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ReadCommand { get; }
    public int RegisterAddress
    {
        get=>_registerAddress;
        set=>this.RaiseAndSetIfChanged(ref _registerAddress,Math.Clamp(value,0,ushort.MaxValue));
    }
    public string RawValue { get=>_rawValue; private set=>this.RaiseAndSetIfChanged(ref _rawValue,value); }
    public string DecimalValue { get=>_decimalValue; private set=>this.RaiseAndSetIfChanged(ref _decimalValue,value); }
    public string Status { get=>_status; private set=>this.RaiseAndSetIfChanged(ref _status,value); }
    public bool IsBusy
    {
        get=>_isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy,value);
            this.RaisePropertyChanged(nameof(CanRead));
        }
    }
    public bool CanRead=>_mainViewModel.IsDeviceConnected && !IsBusy;
    public string DeviceText=>_mainViewModel.IsDeviceConnected
        ? $"设备 {_mainViewModel.DeviceAddress ?? 1} 已连接"
        : "请先在主界面连接设备";

    private async Task ReadAsync()
    {
        if(!CanRead || _mainViewModel.DeviceAddress is not int deviceAddress)
        {
            Status="设备尚未连接";
            return;
        }
        IsBusy=true;
        try
        {
            var values=await _client.ReadHoldingRegistersAsync(
                (byte)deviceAddress,(ushort)RegisterAddress,1);
            var value=values[0];
            RawValue=$"0x{value:X4}";
            DecimalValue=value.ToString();
            Status=$"寄存器 {RegisterAddress}（0x{RegisterAddress:X4}）读取成功";
        }
        catch(TimeoutException)
        {
            Status="读取失败：设备响应超时";
        }
        catch(Exception ex)
        {
            Status=$"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy=false;
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(MainViewModel.IsDeviceConnected) or nameof(MainViewModel.DeviceAddress))
        {
            this.RaisePropertyChanged(nameof(CanRead));
            this.RaisePropertyChanged(nameof(DeviceText));
        }
    }

    public void Dispose()=>_mainViewModel.PropertyChanged-=OnMainViewModelPropertyChanged;
}
