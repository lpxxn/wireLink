using WireLink.Core.Models;
using WireLink.Core.Registers;
using WireLink.Infrastructure.Settings;

namespace WireLink.Tests;

public sealed class WaveformTimingTests
{
    [Fact]
    public void Fault_record_time_decoder_returns_unspecified_strongly_typed_time()
    {
        var value=FaultRecordTimeDecoder.Decode(0x2607,0x2214,0x3009);

        Assert.Equal(new DateTime(2026,7,22,14,30,9,DateTimeKind.Unspecified),value);
        Assert.Equal(DateTimeKind.Unspecified,value.Kind);
        Assert.Equal("2026-07-22 14:30:09",FaultRecordTimeDecoder.Format(value));
    }

    [Theory]
    [InlineData(0x0000,0x0000,0x0000,"为空")]
    [InlineData(0x2A07,0x2214,0x3009,"BCD")]
    [InlineData(0x2602,0x3014,0x3009,"不是有效日期")]
    public void Fault_record_time_decoder_rejects_missing_or_invalid_values(
        int yearMonth,int dayHour,int minuteSecond,string expectedMessage)
    {
        var exception=Assert.Throws<FormatException>(()=>FaultRecordTimeDecoder.Decode(
            (ushort)yearMonth,(ushort)dayHour,(ushort)minuteSecond));

        Assert.Contains(expectedMessage,exception.Message);
    }

    [Fact]
    public void Waveform_absolute_time_uses_first_point_as_start_and_preserves_fractional_milliseconds()
    {
        var points=new[]
        {
            CreatePoint(0,-80),
            CreatePoint(383,39.6875),
        };
        var timing=new WaveformTiming(
            new DateTime(2026,7,22,14,30,59,DateTimeKind.Unspecified),
            987,
            FaultRecordType.Fault,
            1);
        var data=new WaveformData(
            DateTimeOffset.Now,3200,points,0,0,0,WaveformCalibration.FromRegisterValue(0x0204))
        {
            Timing=timing,
        };

        Assert.Equal(new DateTime(2026,7,22,14,30,59,987),data.GetAbsoluteTime(-80));
        Assert.Equal(new DateTime(2026,7,22,14,31,0,67),data.GetAbsoluteTime(0));
        Assert.Equal(
            new DateTime(2026,7,22,14,31,0,DateTimeKind.Unspecified).AddTicks(1_066_875),
            data.WaveformEndTime);
    }

    [Fact]
    public async Task New_fault_time_is_saved_and_same_time_is_reused_after_store_restart()
    {
        var directory=Path.Combine(Path.GetTempPath(),$"wirelink-time-{Guid.NewGuid():N}");
        var path=Path.Combine(directory,"waveform-time-offsets.json");
        try
        {
            var time=new DateTime(2026,7,22,14,30,1,DateTimeKind.Unspecified);
            var firstStore=new JsonWaveformTimeOffsetStore(path,generateMilliseconds:()=>347);
            Assert.Equal(347,await firstStore.GetOrCreateAsync(time));

            var reopenedStore=new JsonWaveformTimeOffsetStore(path,generateMilliseconds:()=>999);
            Assert.Equal(347,await reopenedStore.GetOrCreateAsync(time));

            var json=await File.ReadAllTextAsync(path);
            Assert.Contains("2026-07-22 14:30:01",json);
            Assert.Contains("347",json);
        }
        finally
        {
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }

    [Fact]
    public async Task Concurrent_requests_for_same_time_generate_only_one_offset()
    {
        var directory=Path.Combine(Path.GetTempPath(),$"wirelink-time-{Guid.NewGuid():N}");
        var path=Path.Combine(directory,"waveform-time-offsets.json");
        var generated=0;
        try
        {
            var store=new JsonWaveformTimeOffsetStore(
                path,
                generateMilliseconds:()=>
                {
                    Interlocked.Increment(ref generated);
                    return 456;
                });
            var time=new DateTime(2026,7,22,14,30,1,DateTimeKind.Unspecified);

            var values=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>store.GetOrCreateAsync(time)));

            Assert.All(values,value=>Assert.Equal(456,value));
            Assert.Equal(1,generated);
        }
        finally
        {
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }

    [Fact]
    public async Task Corrupted_offset_file_is_rebuilt_with_a_new_valid_mapping()
    {
        var directory=Path.Combine(Path.GetTempPath(),$"wirelink-time-{Guid.NewGuid():N}");
        var path=Path.Combine(directory,"waveform-time-offsets.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path,"{ damaged json");
            var store=new JsonWaveformTimeOffsetStore(path,generateMilliseconds:()=>678);

            Assert.Equal(678,await store.GetOrCreateAsync(
                new DateTime(2026,7,22,14,30,1,DateTimeKind.Unspecified)));

            var rebuilt=await File.ReadAllTextAsync(path);
            Assert.Contains("2026-07-22 14:30:01",rebuilt);
            Assert.Contains("678",rebuilt);
        }
        finally
        {
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }

    private static WaveformPoint CreatePoint(int sampleIndex,double timeMilliseconds)=>
        new(sampleIndex,sampleIndex/64,sampleIndex%64,timeMilliseconds,0,0,0,0,0,0);
}
