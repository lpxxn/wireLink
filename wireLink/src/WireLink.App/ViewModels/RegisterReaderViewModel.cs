using System.ComponentModel;
using ReactiveUI;
using WireLink.Core.Communication;

namespace WireLink.App.ViewModels;

/// <summary>供现场诊断使用的连续寄存器读取工具。</summary>
public sealed class RegisterReaderViewModel : ViewModelBase, IDisposable
{
    private readonly IModbusRtuClient _client;
    private readonly MainViewModel _mainViewModel;
    private int? _registerAddress;
    private int? _registerCount=1;
    private bool _isBusy;
    private string _rawValue="—";
    private string _decimalValue="—";
    private string _status="输入起始位置和数量后读取";

    public RegisterReaderViewModel(IModbusRtuClient client,MainViewModel mainViewModel)
    {
        _client=client;
        _mainViewModel=mainViewModel;
        _mainViewModel.PropertyChanged+=OnMainViewModelPropertyChanged;
        ReadCommand=ReactiveCommand.CreateFromTask(ReadAsync);
    }

    public ReactiveCommand<System.Reactive.Unit,System.Reactive.Unit> ReadCommand { get; }
    public int? RegisterAddress
    {
        get=>_registerAddress;
        set
        {
            int? normalized=value is null ? null : Math.Clamp(value.Value,0,ushort.MaxValue);
            this.RaiseAndSetIfChanged(ref _registerAddress,normalized);
            this.RaisePropertyChanged(nameof(CanRead));
        }
    }
    public int? RegisterCount
    {
        get=>_registerCount;
        set
        {
            int? normalized=value is null ? null : Math.Clamp(value.Value,1,125);
            this.RaiseAndSetIfChanged(ref _registerCount,normalized);
            this.RaisePropertyChanged(nameof(CanRead));
        }
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
    public bool CanRead=>_mainViewModel.IsDeviceConnected
        && !IsBusy
        && RegisterAddress is not null
        && RegisterCount is not null
        && RegisterAddress.Value+RegisterCount.Value-1<=ushort.MaxValue;
    public string DeviceText=>_mainViewModel.IsDeviceConnected
        ? $"设备 {_mainViewModel.DeviceAddress ?? 1} 已连接"
        : "请先在主界面连接设备";

    private async Task ReadAsync()
    {
        if(RegisterAddress is not int registerAddress || RegisterCount is not int registerCount)
        {
            Status="请输入起始位置和数量";
            return;
        }
        if(!CanRead || _mainViewModel.DeviceAddress is not int deviceAddress)
        {
            Status="设备尚未连接";
            return;
        }
        IsBusy=true;
        try
        {
            var endAddress=registerAddress+registerCount-1;
            if(endAddress>ushort.MaxValue)
            {
                Status="读取失败：起始位置与数量超出寄存器地址范围";
                return;
            }

            var values=await _client.ReadHoldingRegistersAsync(
                (byte)deviceAddress,(ushort)registerAddress,(ushort)registerCount);
            RawValue=string.Join(
                Environment.NewLine,
                values.Select((value,index)=>$"{registerAddress+index}: 0x{value:X4}"));
            DecimalValue=string.Join(
                Environment.NewLine,
                values.Select((value,index)=>$"{registerAddress+index}: {value}"));
            Status=registerCount==1
                ? $"寄存器 {registerAddress}（0x{registerAddress:X4}）读取成功"
                : $"寄存器 {registerAddress}～{endAddress}（共 {registerCount} 个）读取成功";
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
