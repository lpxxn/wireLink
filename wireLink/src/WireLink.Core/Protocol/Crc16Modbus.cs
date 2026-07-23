namespace WireLink.Core.Protocol;

/// <summary>Modbus RTU CRC16，生成多项式为 0xA001，帧中低字节先发送。</summary>
public static class Crc16Modbus
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x0001) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    public static byte[] Append(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 2];
        payload.CopyTo(frame);
        var crc = Compute(payload);
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    public static bool IsValid(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4)
        {
            return false;
        }

        var expected = Compute(frame[..^2]);
        return frame[^2] == (byte)(expected & 0xFF) && frame[^1] == (byte)(expected >> 8);
    }
}

public class ModbusProtocolException : IOException
{
    public ModbusProtocolException(string message) : base(message) { }
}

public sealed class ModbusCrcException : ModbusProtocolException
{
    public ModbusCrcException(string message) : base(message) { }
}

public sealed class ModbusDeviceException : ModbusProtocolException
{
    public ModbusDeviceException(byte exceptionCode)
        : base($"设备返回 Modbus 异常码 0x{exceptionCode:X2}：{Describe(exceptionCode)}")
    {
        ExceptionCode = exceptionCode;
    }

    public byte ExceptionCode { get; }

    private static string Describe(byte code) => code switch
    {
        0x02 => "变量地址出错",
        0x03 => "变量值出错",
        0x04 => "当前没有操作权限",
        _ => "协议未定义的异常",
    };
}
