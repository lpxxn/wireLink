using WireLink.App.ViewModels;
using WireLink.Core.Models;

namespace WireLink.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public void Initial_placeholder_does_not_show_warning_icon()
    {
        var value=new DecodedValue(
            "测试字段",
            [256],
            "—",
            "V",
            "尚未读取",
            [],
            ParseStatus.ReadFailed,
            "尚未读取",
            DateTimeOffset.MinValue);

        var item=new DataItemViewModel(value);

        Assert.False(item.HasWarning);
        Assert.Equal("— V",item.DisplayValue);
    }

    [Fact]
    public void Actual_invalid_data_still_shows_warning_icon()
    {
        var sample=new RawRegisterSample(256,0xFFFF,DateTimeOffset.Now);
        var value=new DecodedValue(
            "测试字段",
            [256],
            "0xFFFF",
            string.Empty,
            "解析失败",
            [sample],
            ParseStatus.InvalidData,
            "无效数据",
            sample.ReadAt);

        Assert.True(new DataItemViewModel(value).HasWarning);
    }
}
