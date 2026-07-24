using System.Buffers.Binary;
using WireLink.Core.Protocol;

namespace WireLink.Simulator;

public enum SimulatorFaultMode { Normal, TimeoutOnce, TimeoutContinuous, BadCrcOnce, ExceptionOnce }

/// <summary>可独立测试的 Modbus RTU 从站协议内核。</summary>
public sealed class SimulatorEngine(byte slaveAddress = 1)
{
    private readonly Dictionary<ushort, ushort> _registers = CreateRegisters();
    private readonly Dictionary<(byte Type, byte Index), ushort[]> _records = CreateRecords();
    private byte _selectedType;
    private byte _selectedIndex;

    public byte SlaveAddress { get; } = slaveAddress;
    public SimulatorFaultMode FaultMode { get; set; }
    public byte ExceptionCode { get; set; } = 0x02;
    public IReadOnlyDictionary<ushort, ushort> Registers => _registers;

    public byte[]? Process(ReadOnlySpan<byte> request)
    {
        if (FaultMode is SimulatorFaultMode.TimeoutOnce or SimulatorFaultMode.TimeoutContinuous)
        {
            if (FaultMode == SimulatorFaultMode.TimeoutOnce) FaultMode = SimulatorFaultMode.Normal;
            return null;
        }
        if (request.Length != 8 || !Crc16Modbus.IsValid(request) || request[0] != SlaveAddress) return null;
        if (FaultMode == SimulatorFaultMode.ExceptionOnce)
        {
            FaultMode = SimulatorFaultMode.Normal;
            return Crc16Modbus.Append([SlaveAddress, (byte)(request[1] | 0x80), ExceptionCode]);
        }

        byte[] response = request[1] switch
        {
            0x03 => Read(request),
            0x06 => Write(request),
            _ => Crc16Modbus.Append([SlaveAddress, (byte)(request[1] | 0x80), 0x02]),
        };
        if (FaultMode == SimulatorFaultMode.BadCrcOnce)
        {
            FaultMode = SimulatorFaultMode.Normal;
            response[^1] ^= 0xFF;
        }
        return response;
    }

    public void Tick()
    {
        var phase = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        _registers[256] = (ushort)(230 + Math.Sin(phase) * 3);
        _registers[257] = (ushort)(231 + Math.Sin(phase + 2.09) * 3);
        _registers[258] = (ushort)(229 + Math.Sin(phase + 4.18) * 3);
        _registers[268] = (ushort)(20 + Math.Abs(Math.Sin(phase)) * 8);
        _registers[269] = (ushort)(19 + Math.Abs(Math.Sin(phase + 2.09)) * 8);
        _registers[270] = (ushort)(21 + Math.Abs(Math.Sin(phase + 4.18)) * 8);
        var energy = ((uint)_registers[432] << 16) | _registers[433];
        energy += 1;
        _registers[432] = (ushort)(energy >> 16);
        _registers[433] = (ushort)energy;
    }

    private byte[] Read(ReadOnlySpan<byte> request)
    {
        var start = BinaryPrimitives.ReadUInt16BigEndian(request[2..4]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(request[4..6]);
        if (count is 0 or > 125) return Crc16Modbus.Append([SlaveAddress, 0x83, 0x03]);
        if (start == 768) LoadSelectedRecord();
        var payload = new byte[3 + count * 2];
        payload[0] = SlaveAddress; payload[1] = 0x03; payload[2] = (byte)(count * 2);
        for (var i = 0; i < count; i++)
        {
            var address = checked((ushort)(start + i));
            if (!_registers.TryGetValue(address, out var value)) return Crc16Modbus.Append([SlaveAddress, 0x83, 0x02]);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3 + i * 2, 2), value);
        }
        return Crc16Modbus.Append(payload);
    }

    private byte[] Write(ReadOnlySpan<byte> request)
    {
        var address = BinaryPrimitives.ReadUInt16BigEndian(request[2..4]);
        var value = BinaryPrimitives.ReadUInt16BigEndian(request[4..6]);
        if (address != 785) return Crc16Modbus.Append([SlaveAddress, 0x86, 0x02]);
        _registers[address] = value;
        _selectedType = (byte)value;
        _selectedIndex = (byte)(value >> 8);
        return request.ToArray();
    }

    private void LoadSelectedRecord()
    {
        if (_records.TryGetValue((_selectedType, _selectedIndex), out var record))
            for (var i = 0; i < record.Length; i++) _registers[(ushort)(768 + i)] = record[i];
        _registers[785] = (ushort)((_selectedIndex << 8) | _selectedType);
    }

    private static Dictionary<ushort, ushort> CreateRegisters()
    {
        var map = new Dictionary<ushort, ushort>();
        foreach (var (start, count) in new[] { (256,3),(268,3),(336,8),(352,6),(432,2),(512,12),(768,21) })
            for (var i = 0; i < count; i++) map[(ushort)(start + i)] = 0;
        map[256]=230; map[257]=231; map[258]=229; map[268]=21; map[269]=20; map[270]=22;
        map[279]=68;
        SetUInt32(map,336,12345); SetUInt32(map,338,12410); SetUInt32(map,340,12280); SetUInt32(map,342,980);
        SetUInt32(map,352,2301); SetUInt32(map,354,2310); SetUInt32(map,356,2294); SetUInt32(map,432,7654321);
        // 暂按实机返回“额定电流序值”模拟：BW1/BW3 的序值 4 都对应 630A，变比为 1。
        map[512]=0x0002; map[784]=0x0444; map[786]=1600; map[787]=4;
        return map;
    }

    private static Dictionary<(byte,byte),ushort[]> CreateRecords()
    {
        var result = new Dictionary<(byte,byte),ushort[]>();
        for (byte type = 0; type <= 2; type++)
        for (byte index = 0; index < 16; index++)
        {
            var record = new ushort[21];
            var secondBcd=(ushort)(((index/10)<<4)|(index%10));
            record[0]=0x2607; record[1]=0x2214; record[2]=(ushort)(0x3000 | secondBcd); record[3]=(ushort)(((type == 1 ? 3 : 7) << 8) | index % 4);
            for (var i=4;i<=11;i++) record[i]=(ushort)(1000 + index * 10 + i);
            record[12]=0x2607; record[13]=0x2208; record[14]=0x1500; record[15]=0x0100; record[16]=0x0444;
            record[17]=(ushort)((index<<8)|type); record[18]=1600; record[19]=4; record[20]=0;
            result[(type,index)] = record;
        }
        return result;
    }

    private static void SetUInt32(Dictionary<ushort,ushort> map, ushort address, uint value)
    { map[address]=(ushort)(value>>16); map[(ushort)(address+1)]=(ushort)value; }
}
