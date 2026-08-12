using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Registers;
using WireLink.Core.Services;

namespace WireLink.Tests;

public sealed class ServiceTests
{
    [Fact]
    public async Task Device_read_keeps_successful_blocks_when_one_block_fails()
    {
        await using var client=new FakeClient((start,count)=>
        {
            if(start==336) throw new TimeoutException("模拟超时");
            return Enumerable.Range(start,count).Select(x=>(ushort)(x==1552?0x030B:x)).ToArray();
        });
        var service=new DeviceDataService(client,new RegisterParser());
        var result=await service.ReadAsync(1,WordOrder.HighWordFirst,BreakerSeries.BW1);
        Assert.Single(result.Errors);
        Assert.Contains(result.Values,x=>x.Name=="A 相电压");
        Assert.DoesNotContain(result.Values,x=>x.Name=="高精度电流测量 Ia");
        Assert.Contains(result.Values,x=>x.Name=="高精度电流测量 Ib");
        Assert.Equal("536",result.Values.Single(x=>x.Name=="A 相电流").Value);
    }

    [Fact]
    public async Task Fault_read_writes_selector_then_reads_record_rated_current_and_operation_count()
    {
        await using var client=new FakeClient((start,count)=>
        {
            if(start==1031)
            {
                Assert.Equal((ushort)1,count);
                return [128];
            }
            if(start==1552)
            {
                Assert.Equal((ushort)1,count);
                return [0x0304];
            }
            Assert.Equal((ushort)768,start); Assert.Equal((ushort)18,count);
            var raw=CreateFaultRecord();
            return raw;
        });
        var result=await new FaultRecordService(client,new RegisterParser()).ReadAsync(
            1,FaultRecordType.Fault,3,WordOrder.HighWordFirst,BreakerSeries.BW1,TimeSpan.Zero);
        Assert.Equal(((ushort)785,(ushort)0x0300),client.LastWrite);
        Assert.Empty(result.Errors); Assert.Equal(16,result.Values.Count);
        Assert.Equal("2026-07-22 14:30:09",
            result.Values.Single(x=>x.Name=="故障记录时间").Value);
        Assert.Equal("630 A",result.Values.Single(x=>x.Name=="额定电流").DisplayValue);
        Assert.Equal("128",result.Values.Single(x=>x.Name=="总操作次数").Value);
    }

    [Fact]
    public async Task Operation_count_failure_does_not_discard_fault_record()
    {
        await using var client=new FakeClient((start,count)=>
        {
            if(start==1031) throw new TimeoutException("1031 超时");
            if(start==1552) return [0x0304];
            return CreateFaultRecord();
        });

        var result=await new FaultRecordService(client,new RegisterParser()).ReadAsync(
            1,FaultRecordType.Fault,0,WordOrder.HighWordFirst,BreakerSeries.BW1,TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.Contains("1031",result.Errors[0]);
        Assert.Contains(result.Values,x=>x.Name=="故障记录时间");
        Assert.DoesNotContain(result.Values,x=>x.Name=="总操作次数");
    }

    [Fact]
    public async Task Fault_record_failure_does_not_discard_operation_count()
    {
        await using var client=new FakeClient((start,count)=>
        {
            if(start==768) throw new TimeoutException("故障记录超时");
            if(start==1552) return [0x0304];
            Assert.Equal((ushort)1031,start);
            return [128];
        });

        var result=await new FaultRecordService(client,new RegisterParser()).ReadAsync(
            1,FaultRecordType.Fault,0,WordOrder.HighWordFirst,BreakerSeries.BW1,TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.Contains("768～785",result.Errors[0]);
        Assert.Equal(2,result.Values.Count);
        Assert.Equal("630 A",result.Values.Single(x=>x.Name=="额定电流").DisplayValue);
        Assert.Equal("128",result.Values.Single(x=>x.Name=="总操作次数").Value);
    }

    [Fact]
    public async Task Rated_current_failure_does_not_discard_fault_record_or_operation_count()
    {
        await using var client=new FakeClient((start,count)=>
        {
            if(start==1552) throw new TimeoutException("1552 超时");
            if(start==1031) return [128];
            return CreateFaultRecord();
        });

        var result=await new FaultRecordService(client,new RegisterParser()).ReadAsync(
            1,FaultRecordType.Fault,0,WordOrder.HighWordFirst,BreakerSeries.BW1,TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.Contains("1552",result.Errors[0]);
        Assert.Contains(result.Values,x=>x.Name=="故障记录时间");
        Assert.Contains(result.Values,x=>x.Name=="总操作次数");
        Assert.DoesNotContain(result.Values,x=>x.Name=="额定电流");
    }

    [Fact]
    public async Task Connection_test_reads_exactly_register_256()
    {
        await using var client=new FakeClient((start,count)=> { Assert.Equal((ushort)256,start); Assert.Equal((ushort)1,count); return [230]; });
        Assert.True(await new DeviceDataService(client,new RegisterParser()).TestConnectionAsync(1));
    }

    [Fact]
    public async Task Waveform_read_requests_18_blocks_and_builds_aligned_points()
    {
        await using var client=new FakeClient((start,count)=>
        {
            var block=WaveformCatalog.Blocks.Single(item=>item.StartAddress==start);
            var phaseOffset=block.Phase switch
            {
                WaveformPhase.A=>0,
                WaveformPhase.B=>1000,
                WaveformPhase.C=>-1000,
                _=>throw new ArgumentOutOfRangeException(),
            };
            return Enumerable.Range(0,count)
                .Select(index=>unchecked((ushort)(short)(phaseOffset+block.SegmentIndex*64+index)))
                .ToArray();
        });
        var progressEvents=new List<WaveformReadProgress>();

        var result=await new WaveformDataService(client).ReadAsync(
            4,new InlineProgress<WaveformReadProgress>(progressEvents.Add));

        Assert.Equal(WaveformCatalog.TotalBlocks,client.ReadRequests.Count);
        Assert.Equal(
            WaveformCatalog.Blocks.Select(block=>(block.StartAddress,block.Count)),
            client.ReadRequests.Select(request=>(request.Start,request.Count)));
        Assert.Equal(384,result.Points.Count);
        Assert.Equal(-80,result.Points[0].TimeMilliseconds);
        Assert.Equal(39.6875,result.Points[^1].TimeMilliseconds);
        Assert.Equal((short)0,result.Points[0].PhaseA);
        Assert.Equal((short)1000,result.Points[0].PhaseB);
        Assert.Equal((short)-1000,result.Points[0].PhaseC);
        Assert.Equal((short)383,result.Points[^1].PhaseA);
        Assert.Equal((short)1383,result.Points[^1].PhaseB);
        Assert.Equal((short)-617,result.Points[^1].PhaseC);
        Assert.Equal((ushort)0xB000,result.Points[0].PhaseAAddress);
        Assert.Equal((ushort)0xB5BF,result.Points[^1].PhaseCAddress);
        Assert.Equal(18,progressEvents.Count);
        Assert.Equal(18,progressEvents[^1].CompletedBlocks);
    }

    [Fact]
    public async Task Waveform_read_stops_on_first_failed_block_without_returning_partial_data()
    {
        await using var client=new FakeClient((start,count)=>
        {
            if(start==0xB240) throw new TimeoutException("模拟录波超时");
            return new ushort[count];
        });

        var exception=await Assert.ThrowsAsync<InvalidOperationException>(
            ()=>new WaveformDataService(client).ReadAsync(4));

        Assert.Contains("B 相",exception.Message);
        Assert.Contains("0xB240",exception.Message);
        Assert.Equal(8,client.ReadRequests.Count);
        Assert.Equal((ushort)0xB240,client.ReadRequests[^1].Start);
    }

    private static ushort[] CreateFaultRecord()
    {
        var raw=new ushort[18];
        raw[0]=0x2607; raw[1]=0x2214; raw[2]=0x3009; raw[3]=0x0700;
        raw[12]=0x2607; raw[13]=0x2208; raw[14]=0x1500; raw[16]=0x0444;
        raw[17]=0x0300;
        return raw;
    }

    private sealed class FakeClient(Func<ushort,ushort,ushort[]> read) : IModbusRtuClient
    {
        public bool IsOpen=>true;
        public (ushort Address,ushort Value) LastWrite { get; private set; }
        public List<(ushort Start,ushort Count)> ReadRequests { get; }=[];
        public ValueTask OpenAsync(SerialConnectionOptions options,CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress,ushort startAddress,ushort count,CancellationToken cancellationToken=default)
        {
            ReadRequests.Add((startAddress,count));
            return Task.FromResult(read(startAddress,count));
        }
        public Task WriteSingleRegisterAsync(byte slaveAddress,ushort address,ushort value,CancellationToken cancellationToken=default){LastWrite=(address,value);return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)=>report(value);
    }
}
