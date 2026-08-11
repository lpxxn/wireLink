using System.Buffers.Binary;
using System.Security.Cryptography;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;

namespace WireLink.Simulator;

/// <summary>
/// 表示从《故障录波协议说明.pdf》的一张 Commix 截图中恢复出的单个 Modbus 03H 录波响应帧。
/// </summary>
/// <param name="StartAddress">
/// 该响应对应的请求起始寄存器地址，例如 B000H、B040H 或 B580H。
/// Modbus 03H 正常响应本身不回显起始地址，因此该值由协议地址目录按帧顺序补充。
/// </param>
/// <param name="Response">
/// 完整的 133 字节响应，包括从机地址、功能码、byte-count、128 字节寄存器数据和 2 字节 CRC。
/// 该字段用于逐字节回归、CRC 校验和模拟器响应来源追踪。
/// </param>
/// <param name="Registers">
/// 从 <paramref name="Response"/> 数据区按 Modbus 大端解析得到的 64 个 <see cref="ushort"/> 原始寄存器值。
/// 此处保留未经 int16 符号转换的 0～65535 位模式；有符号 AD 值应由 Core 层另行解释。
/// </param>
public sealed record PdfWaveformSampleFrame(
    ushort StartAddress,
    ReadOnlyMemory<byte> Response,
    ReadOnlyMemory<ushort> Registers);

/// <summary>
/// 提供《故障录波协议说明.pdf》第 2～11 页中全部 18 个录波响应帧的只读固定样例目录。
/// </summary>
/// <remarks>
/// <para>
/// 该类型的职责是保存并验证 PDF 截图中的原始报文字节，供虚拟串口模拟器和回归测试共同使用。
/// 它不负责串口通信、不重新生成波形、不计算时间轴、不进行 int16 符号转换，也不计算 RMS。
/// </para>
/// <para>
/// 18 帧按 6 个时间段组织，每段依次为 A、B、C 三相：
/// B000H/B040H/B080H、B100H/B140H/B180H，直到 B500H/B540H/B580H。
/// 该顺序与 <see cref="WaveformCatalog.Blocks"/> 一一对应，不能按“先读取全部 A 相，再读取全部 B 相”重新排列。
/// </para>
/// <para>
/// 类首次访问 <see cref="Frames"/> 时会执行 <see cref="CreateFrames"/>。
/// 初始化采用失败即停止策略：总长度、整组 SHA-256、响应头或任意一帧 CRC 不正确时，
/// 静态目录不会返回部分数据，模拟器也不会在不知情的情况下使用损坏样例。
/// </para>
/// <para>
/// PDF 最后的组合曲线只作为视觉参考。本目录以带 CRC 的完整响应帧为精确数据来源，
/// 不对帧边界处的原始跳变进行平滑、倒序或重新排列。
/// </para>
/// </remarks>
public static class PdfWaveformSampleCatalog
{
    /// <summary>
    /// PDF 中每个完整 Modbus 响应帧的固定字节数。
    /// </summary>
    /// <remarks>
    /// 计算方式为：从机地址 1 字节 + 功能码 1 字节 + byte-count 1 字节 +
    /// 64 个寄存器 × 2 字节 + CRC 2 字节 = 133 字节。
    /// </remarks>
    public const int ResponseLength = 133;

    /// <summary>
    /// 18 个完整响应帧按当前固定顺序连续拼接后的 SHA-256 小写十六进制摘要。
    /// </summary>
    /// <remarks>
    /// 该摘要同时锁定每个字节和帧的先后顺序。即使单帧 CRC 仍然有效，只要替换、增加、删除或调换整帧，
    /// 整组摘要也会变化。它用于防止人工 OCR、复制 Base64 或后续维护时发生静默数据漂移。
    /// </remarks>
    public const string SourceSha256 = "1fc84882b21c5dadae9793ac872d314a4aee867571bd5a46af9c414e59a38782";

    /// <summary>
    /// 获取全部 18 个经过长度、摘要、响应头和 CRC 校验的 PDF 录波帧。
    /// </summary>
    /// <remarks>
    /// 属性在类型首次初始化时只创建一次，之后模拟器和测试共享同一份只读帧集合。
    /// 集合中的顺序就是协议规定的实际读取顺序，元素数量应始终等于
    /// <see cref="WaveformCatalog.TotalBlocks"/>。
    /// </remarks>
    public static IReadOnlyList<PdfWaveformSampleFrame> Frames { get; } = CreateFrames();

    /// <summary>
    /// PDF 全部 18 个二进制响应帧连续拼接后的 Base64 源数据。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 使用 Base64 是为了在文本源文件中无损保存任意二进制字节，避免长十六进制数组造成更多人工转录错误。
    /// Base64 不属于 Modbus 协议，也不会发送给真实设备；程序启动时会先将它解码回原始响应字节。
    /// </para>
    /// <para>
    /// 解码结果应为 18 × <see cref="ResponseLength"/> = 2394 字节。
    /// 数据顺序为 B000H、B040H、B080H、B100H、B140H、B180H，依此类推，最后是 B580H。
    /// 对此常量的任何修改都必须同时通过整组 SHA-256、逐帧 CRC 和完整三相单元测试，禁止只修改摘要绕过验证。
    /// </para>
    /// </remarks>
    private const string EncodedResponses = """
BAOAABAAAAAQABAAAAAQACAAEAAQADAAIAAgADAAIAAgABAAEAAgABAAEAAQAAAAEAAQ//D/8AAA//D/8P/w//AAAP/w/+D/0P/g/+D/4P/g/9D/0P/Q/+D/0P/Q/9D/0P/Q/+D/8P/w//D/8P/w//D/8P/w//D/8P/w//AAAAAAABBMhgQDgP/w//D/4P/w/+D/4P/g/+D/4P/Q/+D/4P/Q/+D/4P/Q/9D/4P/Q/+D/8P/w//D/8AAAAAD/8AAQAAAAAP/wAAAAAAAQABAAEAAQABAAIAAgABAAIAAQABAAEAAgABAAEAAQABAAIAAQAAAAAAAAABD/8AAAAAD/8AAA//D/8P/wvqUEA4D//wAAAAEAAAAAAAEAAAABAAEAAAAAAAD//wABAAIAAAAB//8AAP///////wAA//7//wAAAAAAAP//AAIAAP//AAD//wAAAAD//gAA//8AAAAAAAEAAQAB/////wAAAAEAAQAAAAEAAAAA//8AAAABAAEAAP////8AAQAAAAL//yt9BAOAABAAEAAQABAAIAAgABAAIAAgABAAEAAgACAAIAAQABAAIAAgABAAIAAQABAAAAAQAAAAAP/wAAAAAP/w//D/8P/g/+D/4P/g//D/4P/g/9D/0P/A/9D/0P/Q/9D/4P/g/9D/0P/Q/+D/8P/g/+D/4P/g//AAAAAAAAAAAAAQABBw6AQDgAAA//D/8P/g//D/4P/w//D/4P/g//D/8P/w//D/8P/gAAD/8P/w//D/8P/wAAAAAAAAAAAAAAAQABAAEAAQABAAEAAgACAAIAAgACAAMAAgACAAIAAgADAAIAAgACAAIAAwACAAIAAwACAAEAAQABAAEAAQAAAAAAAQAAAAAAAADxoEA4D//wAAAAAAAAABAAAAAAABAAH/////AAEAAQAAAAEAAP//AAH//wABAAEAAf//AAAAAQAAAAEAAQABAAEAAQAA//8AAAAAAAAAAAAAAAEAAAAAAAEAAQABAAEAAAABAAD/////AAD//wAAAAAAAAAAAAEAAAABAAD//wAA//8AAKaHBAOAGjAaMBpAGjAaMBpAGkAaQBpAGjAaQBowGhAYYBegFfAUUBKQEPAPcA3wDGAK4AlwB/AGgAUwA+ACcAEwAAD+wP2Q/HD7UPow+SD4EPbw9fD04PPg8vDyAPEQ8CDvMO5A7XDskOvQ6xDqQOmA6LDn8OdA5pDl0OUg5JDj8OMw4pBdbgQDgBWgFZAVoBWAFYAVgBWQFZAVoBWQFZAVoBWgFFAT0BJwEQAPsA5QDQALsAqACTAIAAbgBaAEkANwAlABQAAw/zD+IP1A/ED7UPpg+YD4kPeg9uD2APUw9GDzsPLg8iDxYPCg7/DvMO6Q7eDtMOyQ6/DrYOrQ6jDpkOkA6GDn0OdQkmkEA4AAAP//AAD//wABAAAAAP//AAAAAQAAAAEAAQABAAEAAQAAAAAAAAABAAEAAf////8AAAAAAAD//wAAAAAAAAABAAEAAQAAAAEAAAAA///////+AAD//wAAAAD//wAA//8AAP////8AAAABAAH//wAAAAD//wAAAAD//wAAAAD//3HBBAOADQAMgAvACzAKkAoQCXAJAAiACBAHgAcABoAGEAWQBSAEoARAA+ADUAMAApACMAHQAXABEADAAGAAAP+w/3D/EP6g/mD+EP3A/ZD9MPzw/LD8YPwQ+9D7oPtg+yD64Pqg+nD6IPng+bD5gPlA+SD48PjA+JD4UPgw+AD30Peg92DVMQQDgAsgCqAKMAmgCTAIsAhAB8AHQAbQBmAGAAWwBUAE4ARwBCADsANAAvACkAJAAfABkAFQAPAAoABQABD/0P+A/0D/AP6w/oD+MP3Q/aD9YP0g/PD8wPyQ/ED78PvA+4D7YPsw+wD64Pqg+nD6QPoQ+eD5wPmQ+WD5QPkQ+QD40PjAx3oEA4AAAP//////////AAAAAAABAAEAAQABAAAAAAAAAAEAAQABAAEAAP//AAD//wAAAAAAAAAAAAAAAP//AAAAAP//////////AAEAAP/+AAD//wAAAAEAAQABAAAAAAAA//////////8AAQABAAEAAQAAAAAAAQABAAEAAP////8AAJwoBAOAA3ADUAMwAxAC0AKwAnACYAJQAiACAAHwAcABoAGAAWABMAEQARAA8ADAALAAkABwAFAAUABAACAAEP/g/8D/oP+Q/5D/cP9g/1D/MP8g/xD/AP7g/uD+0P6w/qD+oP5w/oD+gP5g/mD+MP4g/hD+IP4Q/eD94P3g/dD90P3A/cCuKgQDgAMQAvACsAKQAoACcAIwAhAB8AHQAcABoAGAAWABYAFAASAA8ADQAMAAwACgAJAAYABQAEAAIAAAAAD/4P/Q/9D/sP+g/6D/kP9w/2D/QP8w/zD/IP8Q/wD+8P7Q/sD+sP6g/pD+gP6A/oD+gP5g/mD+QP5A/jD+EP4A/gD+AP3wR2IEA4AAAQACAAIAAP//AAEAAAAAAAEAAQABAAEAAAABAAD//wAAAAEAAAAA//8AAAABAAAAAQACAAEAAQABAAEAAQAAAAAAAf//AAIAAAAB//8AAAAAAAAAAP//AAAAAgABAAIAAQAAAAEAAQAA////////AAAAAAAAAAEAAQABAAIAAUF+BAOAAQAA8wDoANwA0gDIALwAsgCnAJ4AlQCLAH8AdwBsAGUAXABUAEwAQwA8ADQAKgAjABwAFwAPAAgAAf/6//T/7P/l/9//2f/U/87/yf/E/77/uP+0/67/qP+j/57/mv+W/5P/jP+H/4L/fv96/3b/dP9v/23/af9j/1//Xf9b/1e3OAQDgADgANUAywDBALUArACjAJkAkwCIAIAAeABwAGkAYQBZAFEASgBDADsANAAsACgAIAAaABIACwAF//7/+f/1/+//6v/l/+D/2//W/9D/zP/G/8P/vf+5/7T/sP+t/6j/ov+e/5z/mP+U/5H/jv+K/4f/hP9//3v/ef93/3P/b/9tg08EA4AAAQACAAAAAQAAAAEAAQAAAAEAAAAAAAAAAAABAAEAAAAAAAAAAAAAAAAAAQABAAEAAf////////////8AAAABAAEAAQAAAAAAAQABAAD//wAA//8AAAAAAAAAAf///////wAAAAAAAAABAAAAAQAAAAH//wAAAAEAAgAAAAAAACmT
""";

    /// <summary>
    /// 将 <see cref="EncodedResponses"/> 中保存的 PDF 原始响应数据还原为 18 个可供模拟器读取的录波帧。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Base64 只用于把二进制报文安全地保存在 C# 源文件中，并不是设备协议的一部分。
    /// 解码后的内容是 PDF 第 2～11 页截图中的 18 个完整 Modbus 03H 响应帧，排列顺序必须与
    /// <see cref="WaveformCatalog.Blocks"/> 完全一致：每个时间段依次为 A、B、C 三相。
    /// </para>
    /// <para>
    /// 每帧固定 133 字节：从机地址 1 字节、功能码 1 字节、byte-count 1 字节、
    /// 64 个寄存器共 128 字节，以及 CRC 2 字节。方法会依次检查总长度、整组 SHA-256、
    /// 每帧响应头和每帧 CRC，任何一步失败都会立即抛出异常，避免损坏或转录错误的数据进入模拟器。
    /// </para>
    /// <para>
    /// 寄存器在此处只按 Modbus 大端顺序还原成 <see cref="ushort"/>，不进行有符号转换。
    /// 这样可以完整保留线路上的 16 位原始位模式；例如 FEC0H 在这里保存为 65216，
    /// 需要作为 AD 值使用时，再由 Core 层按 int16 二进制补码解释为 -320。
    /// </para>
    /// <para>
    /// Modbus 03H 正常响应不会回显请求的起始寄存器地址，因此每帧的
    /// <see cref="PdfWaveformSampleFrame.StartAddress"/> 只能根据帧序号从
    /// <see cref="WaveformCatalog.Blocks"/> 取得。整组 SHA-256 和显式顺序单元测试共同防止帧顺序被误改。
    /// </para>
    /// </remarks>
    /// <returns>
    /// 18 个经过完整性验证的帧。每个元素同时保留完整响应字节和解析后的 64 个原始寄存器值。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Base64 数据总长度不正确、整组 SHA-256 不匹配、某帧响应头错误或某帧 CRC 校验失败时抛出。
    /// </exception>
    private static IReadOnlyList<PdfWaveformSampleFrame> CreateFrames()
    {
        // 第 1 步：把源代码中的 Base64 文本恢复为连续二进制数据。
        // 此时 allBytes 的布局是：第 1 帧 133 字节 + 第 2 帧 133 字节 + ... + 第 18 帧 133 字节。
        var allBytes = Convert.FromBase64String(EncodedResponses);

        // 第 2 步：先校验总长度。18 帧中任何一帧少一个字节或多一个字节，都会导致后续切帧整体错位。
        var expectedLength = WaveformCatalog.TotalBlocks * ResponseLength;
        if (allBytes.Length != expectedLength)
            throw new InvalidOperationException($"PDF 录波帧总长度错误：期望 {expectedLength}，收到 {allBytes.Length}。");

        // 第 3 步：校验 18 帧连续字节的整体摘要。
        // CRC 只能验证单帧传输格式；固定 SHA-256 还可以发现帧顺序变化、整帧替换或源数据被重新转录。
        var hash = Convert.ToHexString(SHA256.HashData(allBytes)).ToLowerInvariant();
        if (!string.Equals(hash, SourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"PDF 录波帧摘要错误：期望 {SourceSha256}，收到 {hash}。");

        // 第 4 步：按照固定长度逐帧切分。数组长度来自协议目录，当前为 6 个时间段 × 3 相 = 18 帧。
        var frames = new PdfWaveformSampleFrame[WaveformCatalog.TotalBlocks];
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            // ToArray() 为当前帧创建独立存储，避免返回的 ReadOnlyMemory 继续引用整组大数组。
            var response = allBytes.AsSpan(frameIndex * ResponseLength, ResponseLength).ToArray();

            // 第 5 步：检查 Modbus 正常响应头：
            // response[0] = 04H 从机地址；response[1] = 03H 功能码；response[2] = 80H 数据字节数。
            // 80H 等于 128 字节，也就是 64 个 16 位寄存器。
            if (response[0] != 0x04 || response[1] != 0x03 || response[2] != 0x80)
                throw new InvalidOperationException($"PDF 第 {frameIndex + 1} 帧响应头错误。");

            // 第 6 步：校验当前完整帧末尾的 Modbus CRC16，确保截图转录得到的单帧字节自洽。
            if (!Crc16Modbus.IsValid(response))
                throw new InvalidOperationException($"PDF 第 {frameIndex + 1} 帧 CRC 错误。");

            // 第 7 步：从 response[3] 开始，每两个字节还原一个寄存器。
            // 数据区范围是 response[3..130]，response[131..132] 是 CRC，不能当作采样值解析。
            var registers = new ushort[WaveformCatalog.SamplesPerBlock];
            for (var registerIndex = 0; registerIndex < registers.Length; registerIndex++)
            {
                // 厂商已确认线路采用大端结构，因此高字节在前、低字节在后。
                // 这里故意返回 ushort 原始值，不在模拟器样例目录中提前转换为 short。
                registers[registerIndex] = BinaryPrimitives.ReadUInt16BigEndian(
                    response.AsSpan(3 + registerIndex * 2, 2));
            }

            // 第 8 步：03H 响应本身不包含起始地址，必须用相同序号关联协议目录中的地址。
            // 例如 frameIndex=0 对应 B000H，frameIndex=1 对应 B040H，最后一帧对应 B580H。
            frames[frameIndex] = new PdfWaveformSampleFrame(
                WaveformCatalog.Blocks[frameIndex].StartAddress,
                response,
                registers);
        }

        // 所有帧都通过校验后才返回；静态目录初始化失败时，模拟器不会使用部分或未校验的数据。
        return frames;
    }
}
