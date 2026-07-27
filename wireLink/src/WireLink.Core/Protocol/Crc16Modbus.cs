namespace WireLink.Core.Protocol;

/// <summary>
/// 提供 Modbus RTU 使用的 CRC-16 校验计算、追加和验证功能。
/// </summary>
/// <remarks>
/// <para>
/// Modbus RTU 使用 CRC-16/MODBUS：初始值为 <c>0xFFFF</c>，按最低有效位优先处理，
/// 反射形式多项式为 <c>0xA001</c>（正向形式为 <c>0x8005</c>），结果不执行最终异或。
/// </para>
/// <para>
/// <see cref="Compute"/> 返回一个 <see cref="ushort"/> 数值，但写入 RTU 帧时必须先放低字节、
/// 再放高字节。例如 CRC 数值为 <c>0xCDC5</c>，帧尾的两个字节应为 <c>C5 CD</c>。
/// </para>
/// <para>
/// CRC 的计算范围是从从机地址开始，到最后一个数据字节结束，不包含帧尾已有的两个 CRC 字节。
/// 标准请求 <c>01 03 00 00 00 0A</c> 追加 CRC 后为
/// <c>01 03 00 00 00 0A C5 CD</c>。
/// </para>
/// </remarks>
public static class Crc16Modbus
{
    /// <summary>
    /// 计算指定数据的 Modbus CRC-16 数值。
    /// </summary>
    /// <param name="data">
    /// 需要参与校验的数据，通常是尚未包含 CRC 的 Modbus RTU 帧内容。
    /// 该方法只读取传入数据，不会修改原缓冲区。
    /// </param>
    /// <returns>
    /// 计算得到的 16 位 CRC 数值。调用方写入 RTU 帧时应先写返回值的低 8 位，
    /// 再写高 8 位。
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        // CRC-16/MODBUS 规定所有位初始为 1。
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            // 当前输入字节与 CRC 的低 8 位对齐并异或。
            // 由于算法按最低有效位优先处理，不需要先反转输入字节。
            crc ^= value;

            // 一个输入字节包含 8 位，因此逐位执行 8 次右移和多项式运算。
            for (var bit = 0; bit < 8; bit++)
            {
                // 如果移位前最低位为 1，右移后需要与反射多项式 0xA001 异或；
                // 最低位为 0 时只右移。显式转换为 ushort，保证结果始终为 16 位。
                crc = (crc & 0x0001) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    /// <summary>
    /// 为不含 CRC 的 Modbus RTU 帧内容计算校验值，并返回带有两个 CRC 尾字节的新数组。
    /// </summary>
    /// <param name="payload">不包含 CRC 的帧内容。</param>
    /// <returns>
    /// 新创建的完整帧。原内容保持不变，返回数组最后两个字节依次为 CRC 低字节和高字节。
    /// </returns>
    public static byte[] Append(ReadOnlySpan<byte> payload)
    {
        // 不直接修改调用方数据；额外分配两个字节保存 CRC。
        var frame = new byte[payload.Length + 2];
        payload.CopyTo(frame);
        var crc = Compute(payload);

        // Modbus RTU 在线路上的 CRC 字节顺序固定为低字节在前、高字节在后。
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>
    /// 验证完整 Modbus RTU 帧末尾的 CRC 是否与帧内容匹配。
    /// </summary>
    /// <param name="frame">
    /// 包含帧内容以及末尾两个 CRC 字节的完整 RTU 帧。
    /// </param>
    /// <returns>
    /// CRC 低、高字节均匹配时返回 <see langword="true"/>；帧过短或校验不匹配时返回
    /// <see langword="false"/>。
    /// </returns>
    public static bool IsValid(ReadOnlySpan<byte> frame)
    {
        // 一个可校验的 RTU 帧至少需要地址、功能码和两个 CRC 字节。
        if (frame.Length < 4)
        {
            return false;
        }

        // 排除最后两个已接收的 CRC 字节，重新计算前面全部帧内容。
        var expected = Compute(frame[..^2]);

        // 分别比较低字节和高字节，严格遵守 Modbus RTU 的 CRC 传输顺序。
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
