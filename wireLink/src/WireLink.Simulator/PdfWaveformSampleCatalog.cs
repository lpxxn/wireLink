using System.Buffers.Binary;
using System.Security.Cryptography;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;

namespace WireLink.Simulator;

/// <summary>《故障录波协议说明.pdf》中 18 个完整响应帧及其大端 int16 寄存器值。</summary>
public sealed record PdfWaveformSampleFrame(
    ushort StartAddress,
    ReadOnlyMemory<byte> Response,
    ReadOnlyMemory<ushort> Registers);

/// <summary>
/// PDF 第 2～11 页的单相故障固定样例。帧按 <see cref="WaveformCatalog.Blocks"/> 顺序拼接，
/// 每帧 133 字节：04 03 80、128 字节数据、2 字节 CRC。
/// </summary>
public static class PdfWaveformSampleCatalog
{
    public const int ResponseLength = 133;
    public const string SourceSha256 = "1fc84882b21c5dadae9793ac872d314a4aee867571bd5a46af9c414e59a38782";

    public static IReadOnlyList<PdfWaveformSampleFrame> Frames { get; } = CreateFrames();

    private const string EncodedResponses = """
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/6D/kP9w/2D/QP8w/zD/IP8Q/wD+8P7Q/sD+sP6g/pD+gP6A/oD+gP5g/mD+QP5A/jD+EP4A/gD+AP3wR2IEA4AAAQACAAIAAP//AAEAAAAAAAEAAQABAAEAAAABAAD//wAAAAEAAAAA//8AAAABAAAAAQACAAEAAQABAAEAAQAAAAAAAf//AAIAAAAB//8AAAAAAAAAAP//AAAAAgABAAIAAQAAAAEAAQAA////////AAAAAAAAAAEAAQABAAIAAUF+BAOAAQAA8wDoANwA0gDIALwAsgCnAJ4AlQCLAH8AdwBsAGUAXABUAEwAQwA8ADQAKgAjABwAFwAPAAgAAf/6//T/7P/l/9//2f/U/87/yf/E/77/uP+0/67/qP+j/57/mv+W/5P/jP+H/4L/fv96/3b/dP9v/23/af9j/1//Xf9b/1e3OAQDgADgANUAywDBALUArACjAJkAkwCIAIAAeABwAGkAYQBZAFEASgBDADsANAAsACgAIAAaABIACwAF//7/+f/1/+//6v/l/+D/2//W/9D/zP/G/8P/vf+5/7T/sP+t/6j/ov+e/5z/mP+U/5H/jv+K/4f/hP9//3v/ef93/3P/b/9tg08EA4AAAQACAAAAAQAAAAEAAQAAAAEAAAAAAAAAAAABAAEAAAAAAAAAAAAAAAAAAQABAAEAAf////////////8AAAABAAEAAQAAAAAAAQABAAD//wAA//8AAAAAAAAAAf///////wAAAAAAAAABAAAAAQAAAAH//wAAAAEAAgAAAAAAACmT
""";

    private static IReadOnlyList<PdfWaveformSampleFrame> CreateFrames()
    {
        var allBytes = Convert.FromBase64String(EncodedResponses);
        var expectedLength = WaveformCatalog.TotalBlocks * ResponseLength;
        if (allBytes.Length != expectedLength)
            throw new InvalidOperationException($"PDF 录波帧总长度错误：期望 {expectedLength}，收到 {allBytes.Length}。");

        var hash = Convert.ToHexString(SHA256.HashData(allBytes)).ToLowerInvariant();
        if (!string.Equals(hash, SourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"PDF 录波帧摘要错误：期望 {SourceSha256}，收到 {hash}。");

        var frames = new PdfWaveformSampleFrame[WaveformCatalog.TotalBlocks];
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var response = allBytes.AsSpan(frameIndex * ResponseLength, ResponseLength).ToArray();
            if (response[0] != 0x04 || response[1] != 0x03 || response[2] != 0x80)
                throw new InvalidOperationException($"PDF 第 {frameIndex + 1} 帧响应头错误。");
            if (!Crc16Modbus.IsValid(response))
                throw new InvalidOperationException($"PDF 第 {frameIndex + 1} 帧 CRC 错误。");

            var registers = new ushort[WaveformCatalog.SamplesPerBlock];
            for (var registerIndex = 0; registerIndex < registers.Length; registerIndex++)
                registers[registerIndex] = BinaryPrimitives.ReadUInt16BigEndian(
                    response.AsSpan(3 + registerIndex * 2, 2));

            frames[frameIndex] = new PdfWaveformSampleFrame(
                WaveformCatalog.Blocks[frameIndex].StartAddress,
                response,
                registers);
        }
        return frames;
    }
}
