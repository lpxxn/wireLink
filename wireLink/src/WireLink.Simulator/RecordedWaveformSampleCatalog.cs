using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using WireLink.Core.Registers;

namespace WireLink.Simulator;

public sealed record RecordedWaveformBlock(ushort StartAddress, IReadOnlyList<ushort> Registers);

/// <summary>用户提供的16列录波原始点明细，保留每个寄存器的原始16位位模式。</summary>
public static class RecordedWaveformSampleCatalog
{
    public const string SourceFileName = "录波原始点明细_20261104_084756.xlsx";
    public const string SourceSha256 = "4b6f65229d45fb5c948221bea5030e42722c3a182ce318b3de85a29f230e568a";
    public const string RegisterSha256 = "ed2e6e7c7dacecadddd42fbf84f380325d690733e6eddd15537c4749922acc43";

    public static IReadOnlyList<RecordedWaveformBlock> Blocks { get; } = Load();

    private static IReadOnlyList<RecordedWaveformBlock> Load()
    {
        using var stream = typeof(RecordedWaveformSampleCatalog).Assembly.GetManifestResourceStream(
            "WireLink.Simulator.Samples.recorded-waveform-20261104.json")
            ?? throw new InvalidOperationException("内置录波明细样本缺失。");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("sourceFileName").GetString() != SourceFileName ||
            root.GetProperty("sourceSha256").GetString() != SourceSha256 ||
            root.GetProperty("registerSha256").GetString() != RegisterSha256)
            throw new InvalidOperationException("录波明细样本来源标识错误。");

        var entries = root.GetProperty("blocks");
        if (entries.GetArrayLength() != WaveformCatalog.TotalBlocks)
            throw new InvalidOperationException("录波明细样本必须包含18个块。");
        var blocks = new List<RecordedWaveformBlock>();
        var bytes = new byte[WaveformCatalog.TotalBlocks * WaveformCatalog.SamplesPerBlock * sizeof(ushort)];
        var offset = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var definition = WaveformCatalog.Blocks[blocks.Count];
            var address = entry.GetProperty("startAddress").GetUInt16();
            var values = entry.GetProperty("values").EnumerateArray().Select(value => value.GetUInt16()).ToArray();
            if (address != definition.StartAddress || values.Length != definition.Count)
                throw new InvalidOperationException($"录波明细样本地址或点数错误：{address:X4}H。");
            foreach (var value in values)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, sizeof(ushort)), value);
                offset += sizeof(ushort);
            }
            blocks.Add(new RecordedWaveformBlock(address, Array.AsReadOnly(values)));
        }
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != RegisterSha256)
            throw new InvalidOperationException("录波明细样本校验失败，不允许静默修改原始值。");
        return blocks.AsReadOnly();
    }
}
