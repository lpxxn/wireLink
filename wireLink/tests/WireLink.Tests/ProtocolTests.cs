using System.Buffers.Binary;
using WireLink.Core.Communication;
using WireLink.Core.Protocol;

namespace WireLink.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Crc_matches_standard_modbus_example()
    {
        var frame=Crc16Modbus.Append([0x01,0x03,0x00,0x00,0x00,0x0A]);
        Assert.Equal("01030000000AC5CD",Convert.ToHexString(frame));
        Assert.True(Crc16Modbus.IsValid(frame));
    }

    [Fact]
    public async Task Client_accepts_fragmented_response_and_big_endian_registers()
    {
        await using var transport=new ScriptedTransport(request =>
        {
            var payload=new byte[]{request[0],0x03,0x04,0x12,0x34,0xAB,0xCD};
            return Crc16Modbus.Append(payload);
        },chunkSize:1);
        await using var client=new ModbusRtuClient(transport);
        await client.OpenAsync(new("test",9600,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1)));
        Assert.Equal([0x1234,0xABCD],await client.ReadHoldingRegistersAsync(1,256,2));
    }

    [Fact]
    public async Task Client_retries_one_bad_crc_then_succeeds()
    {
        var calls=0;
        await using var transport=new ScriptedTransport(request =>
        {
            calls++;
            var response=Crc16Modbus.Append([request[0],0x03,0x02,0x00,0x2A]);
            if(calls==1) response[^1]^=0xFF;
            return response;
        });
        await using var client=new ModbusRtuClient(transport);
        await client.OpenAsync(new("test",9600,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1)));
        Assert.Equal((ushort)42,(await client.ReadHoldingRegistersAsync(1,256,1))[0]);
        Assert.Equal(2,calls);
    }

    [Fact]
    public async Task Device_exception_is_not_retried()
    {
        var calls=0;
        await using var transport=new ScriptedTransport(request=> { calls++; return Crc16Modbus.Append([request[0],0x83,0x02]); });
        await using var client=new ModbusRtuClient(transport);
        await client.OpenAsync(new("test",9600,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1)));
        var error=await Assert.ThrowsAsync<ModbusDeviceException>(()=>client.ReadHoldingRegistersAsync(1,1,1));
        Assert.Equal((byte)2,error.ExceptionCode); Assert.Equal(1,calls);
    }

    private sealed class ScriptedTransport(Func<byte[],byte[]> responder,int chunkSize=int.MaxValue) : IByteTransport
    {
        private readonly Queue<byte> _receive=[];
        public bool IsOpen { get; private set; }
        public ValueTask OpenAsync(SerialConnectionOptions options,CancellationToken cancellationToken=default){IsOpen=true;return ValueTask.CompletedTask;}
        public ValueTask CloseAsync(CancellationToken cancellationToken=default){IsOpen=false;return ValueTask.CompletedTask;}
        public ValueTask DiscardInputAsync(CancellationToken cancellationToken=default){_receive.Clear();return ValueTask.CompletedTask;}
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default){foreach(var b in responder(buffer.ToArray()))_receive.Enqueue(b);return ValueTask.CompletedTask;}
        public ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            var count=Math.Min(Math.Min(buffer.Length,chunkSize),_receive.Count);
            for(var i=0;i<count;i++)buffer.Span[i]=_receive.Dequeue();
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
