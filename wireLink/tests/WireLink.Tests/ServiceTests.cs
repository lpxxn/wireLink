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
            return Enumerable.Range(start,count).Select(x=>(ushort)(x==787?11:x)).ToArray();
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
    public async Task Fault_read_writes_selector_then_reads_768_through_787()
    {
        await using var client=new FakeClient((start,count)=>
        {
            if(start==1031)
            {
                Assert.Equal((ushort)1,count);
                return [128];
            }
            Assert.Equal((ushort)768,start); Assert.Equal((ushort)20,count);
            var raw=new ushort[20]; raw[0]=0x2607; raw[1]=0x2214; raw[2]=0x3009; raw[3]=0x0700;
            raw[12]=0x2607; raw[13]=0x2208; raw[14]=0x1500; raw[16]=0x0444; raw[17]=0x0300; raw[18]=1600; raw[19]=4;
            return raw;
        });
        var result=await new FaultRecordService(client,new RegisterParser()).ReadAsync(
            1,FaultRecordType.Fault,3,WordOrder.HighWordFirst,BreakerSeries.BW1,TimeSpan.Zero);
        Assert.Equal(((ushort)785,(ushort)0x0300),client.LastWrite);
        Assert.Empty(result.Errors); Assert.Equal(17,result.Values.Count);
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
            Assert.Equal((ushort)1031,start);
            return [128];
        });

        var result=await new FaultRecordService(client,new RegisterParser()).ReadAsync(
            1,FaultRecordType.Fault,0,WordOrder.HighWordFirst,BreakerSeries.BW1,TimeSpan.Zero);

        Assert.Single(result.Errors);
        Assert.Contains("768～787",result.Errors[0]);
        Assert.Single(result.Values);
        Assert.Equal("总操作次数",result.Values[0].Name);
        Assert.Equal("128",result.Values[0].Value);
    }

    [Fact]
    public async Task Connection_test_reads_exactly_register_256()
    {
        await using var client=new FakeClient((start,count)=> { Assert.Equal((ushort)256,start); Assert.Equal((ushort)1,count); return [230]; });
        Assert.True(await new DeviceDataService(client,new RegisterParser()).TestConnectionAsync(1));
    }

    private static ushort[] CreateFaultRecord()
    {
        var raw=new ushort[20];
        raw[0]=0x2607; raw[1]=0x2214; raw[2]=0x3009; raw[3]=0x0700;
        raw[12]=0x2607; raw[13]=0x2208; raw[14]=0x1500; raw[16]=0x0444;
        raw[17]=0x0300; raw[18]=1600; raw[19]=4;
        return raw;
    }

    private sealed class FakeClient(Func<ushort,ushort,ushort[]> read) : IModbusRtuClient
    {
        public bool IsOpen=>true;
        public (ushort Address,ushort Value) LastWrite { get; private set; }
        public ValueTask OpenAsync(SerialConnectionOptions options,CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken=default)=>ValueTask.CompletedTask;
        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress,ushort startAddress,ushort count,CancellationToken cancellationToken=default)=>Task.FromResult(read(startAddress,count));
        public Task WriteSingleRegisterAsync(byte slaveAddress,ushort address,ushort value,CancellationToken cancellationToken=default){LastWrite=(address,value);return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
