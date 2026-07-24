using ReactiveUI;
using WireLink.Core.Models;

namespace WireLink.App.ViewModels;

public sealed class DataItemViewModel : ViewModelBase
{
    private DecodedValue _value;
    public DataItemViewModel(DecodedValue value) => _value = value;
    public string Name => _value.Name;
    public string DisplayValue => _value.DisplayValue;
    public string Tooltip => $"地址：{_value.AddressText}\n公式：{_value.Formula}\n原始：{_value.RawText}";
    public string Warning => _value.Warning ?? string.Empty;
    /// <summary>
    /// 初始占位值已经用“—”表达尚未读取，不再重复显示黄色警告图标。
    /// 实际解析警告、无效数据和读取后过期等状态仍显示图标。
    /// </summary>
    public bool HasWarning => _value.Status is not ParseStatus.Success
        && !(_value.Status == ParseStatus.ReadFailed
             && _value.Value == "—"
             && _value.RawSamples.Count == 0);
    public DecodedValue Value => _value;

    public void Update(DecodedValue value)
    {
        _value = value;
        this.RaisePropertyChanged(string.Empty);
    }

    public void MarkStale(string warning)
    {
        _value = _value with { Status = ParseStatus.Stale, Warning = warning };
        this.RaisePropertyChanged(string.Empty);
    }
}

public sealed record DataRowViewModel(DataItemViewModel Left, DataItemViewModel? Right);

public sealed record ExportRequest(string Title, IReadOnlyList<DecodedValue> Values,
    DateTimeOffset ReadAt, FaultRecordType? RecordType = null, byte? RecordIndex = null);
