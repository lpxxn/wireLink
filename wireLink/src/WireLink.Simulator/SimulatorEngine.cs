using System.Buffers.Binary;
using WireLink.Core.Protocol;

namespace WireLink.Simulator;

public enum SimulatorFaultMode { Normal, TimeoutOnce, TimeoutContinuous, BadCrcOnce, ExceptionOnce }
public enum SimulatorCurrentEventMode { Normal, Fault, Alarm }

/// <summary>可独立测试的 Modbus RTU 从站协议内核。</summary>
public sealed class SimulatorEngine(byte slaveAddress = 1)
{
    private readonly object _sync = new();
    private readonly Dictionary<ushort, ushort> _registers = CreateRegisters();
    private readonly Dictionary<(byte Type, byte Index), ushort[]> _records = CreateRecords();
    private byte _selectedType;
    private byte _selectedIndex;

    public byte SlaveAddress { get; } = slaveAddress;
    public SimulatorFaultMode FaultMode { get; set; }
    public SimulatorCurrentEventMode CurrentEventMode { get; private set; } = SimulatorCurrentEventMode.Normal;
    public byte ExceptionCode { get; set; } = 0x02;
    public IReadOnlyDictionary<ushort, ushort> Registers => _registers;
    public int RegisterCount { get { lock (_sync) return _registers.Count; } }

    public void SetCurrentEvent(SimulatorCurrentEventMode mode)
    {
        lock (_sync)
        {
            CurrentEventMode = mode;
            LoadCurrentEvent();
        }
    }

    public byte[]? Process(ReadOnlySpan<byte> request)
    {
        lock (_sync)
        {
            return ProcessCore(request);
        }
    }

    private byte[]? ProcessCore(ReadOnlySpan<byte> request)
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
        lock (_sync)
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
        // 1552.bit0～bit7=4，对应 BW1/BW3 的 630A；bit8～bit11=3 模拟非零框架等级。
        map[512]=0x0002; map[784]=0x0444; map[786]=1600; map[1552]=0x0304; map[1031]=128;
        return map;
    }

    private void LoadCurrentEvent()
    {
        // 清空当前事件区：513～514 当前报警位图，515 类型/相别，516～523 数据 0～7。
        _registers[512] = 0x0002;
        for (ushort address = 513; address <= 523; address++) _registers[address] = 0;

        switch (CurrentEventMode)
        {
            case SimulatorCurrentEventMode.Fault:
                // 运行状态 bit3=故障跳闸，bit10=新故障；515 高字节 07H=过载故障，低字节 00H=A相。
                _registers[512] = 0x0002 | (1 << 3) | (1 << 10);
                _registers[515] = 0x0700;
                _registers[516] = 125;
                _registers[517] = 10;
                _registers[518] = 0;
                _registers[519] = 160;
                _registers[520] = 125;
                _registers[521] = 121;
                _registers[522] = 118;
                _registers[523] = 5;
                break;
            case SimulatorCurrentEventMode.Alarm:
                // 运行状态 bit2=有报警，bit11=新报警；当前报警 bit2=过载预报警。
                _registers[512] = 0x0002 | (1 << 2) | (1 << 11);
                _registers[513] = 1 << 2;
                _registers[514] = 0;
                _registers[515] = 0x0300;
                _registers[516] = 125;
                break;
            case SimulatorCurrentEventMode.Normal:
            default:
                break;
        }
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
