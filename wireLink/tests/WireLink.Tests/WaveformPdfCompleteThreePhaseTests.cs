using System.Diagnostics;
using WireLink.Core.Models;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;
using WireLink.Simulator;
using Xunit.Abstractions;

namespace WireLink.Tests;

/// <summary>
/// 将 PDF 的 18 个响应块组织成可在调试器中完整展开的三相录波快照。
/// 运行测试后，可在 Test Explorer 的 Output 中查看全部 18×64 点和 384 行三相对齐数据。
/// </summary>
public sealed class WaveformPdfCompleteThreePhaseTests(ITestOutputHelper output)
{
    private static readonly ExpectedBlock[] DocumentedBlockOrder =
    [
        new(1, 0, -80, WaveformPhase.A, 0xB000),
        new(2, 0, -80, WaveformPhase.B, 0xB040),
        new(3, 0, -80, WaveformPhase.C, 0xB080),
        new(4, 1, -60, WaveformPhase.A, 0xB100),
        new(5, 1, -60, WaveformPhase.B, 0xB140),
        new(6, 1, -60, WaveformPhase.C, 0xB180),
        new(7, 2, -40, WaveformPhase.A, 0xB200),
        new(8, 2, -40, WaveformPhase.B, 0xB240),
        new(9, 2, -40, WaveformPhase.C, 0xB280),
        new(10, 3, -20, WaveformPhase.A, 0xB300),
        new(11, 3, -20, WaveformPhase.B, 0xB340),
        new(12, 3, -20, WaveformPhase.C, 0xB380),
        new(13, 4, 0, WaveformPhase.A, 0xB400),
        new(14, 4, 0, WaveformPhase.B, 0xB440),
        new(15, 4, 0, WaveformPhase.C, 0xB480),
        new(16, 5, 20, WaveformPhase.A, 0xB500),
        new(17, 5, 20, WaveformPhase.B, 0xB540),
        new(18, 5, 20, WaveformPhase.C, 0xB580),
    ];

    [Fact]
    public void Pdf_all_18_blocks_follow_documented_time_then_phase_order()
    {
        Assert.Equal(18, DocumentedBlockOrder.Length);
        Assert.Equal(18, WaveformCatalog.Blocks.Count);
        Assert.Equal(18, PdfWaveformSampleCatalog.Frames.Count);
        Assert.Equal(
            "1fc84882b21c5dadae9793ac872d314a4aee867571bd5a46af9c414e59a38782",
            PdfWaveformSampleCatalog.SourceSha256);

        for (var index = 0; index < DocumentedBlockOrder.Length; index++)
        {
            var expected = DocumentedBlockOrder[index];
            var catalogBlock = WaveformCatalog.Blocks[index];
            var pdfFrame = PdfWaveformSampleCatalog.Frames[index];

            Assert.Equal(index + 1, expected.ReadOrder);
            Assert.Equal(expected.SegmentIndex, catalogBlock.SegmentIndex);
            Assert.Equal(expected.SegmentStartMilliseconds, catalogBlock.SegmentStartMilliseconds);
            Assert.Equal(expected.Phase, catalogBlock.Phase);
            Assert.Equal(expected.StartAddress, catalogBlock.StartAddress);
            Assert.Equal(expected.StartAddress, pdfFrame.StartAddress);
            Assert.Equal(64, pdfFrame.Registers.Length);
            Assert.Equal(133, pdfFrame.Response.Length);
            Assert.True(Crc16Modbus.IsValid(pdfFrame.Response.Span));
        }
    }

    [Fact]
    public void Pdf_complete_three_phase_snapshot_exposes_every_sample_for_debugging()
    {
        var snapshot = CreateDebugSnapshot();

        // 在下一行设置断点，可直接展开 snapshot.Blocks、PhaseA、PhaseB、PhaseC 和 Points。
        Assert.Equal(18, snapshot.Blocks.Count);
        Assert.Equal(384, snapshot.PhaseA.Count);
        Assert.Equal(384, snapshot.PhaseB.Count);
        Assert.Equal(384, snapshot.PhaseC.Count);
        Assert.Equal(384, snapshot.Points.Count);

        Assert.Equal(1152, snapshot.Blocks.Sum(block => block.Samples.Count));
        Assert.All(snapshot.Blocks, block => Assert.Equal(64, block.Samples.Count));
        Assert.Equal(2142.413718, WaveformSampleDecoder.CalculateRms(snapshot.PhaseA), 6);
        Assert.Equal(1786.377408, WaveformSampleDecoder.CalculateRms(snapshot.PhaseB), 6);
        Assert.Equal(0.835414, WaveformSampleDecoder.CalculateRms(snapshot.PhaseC), 6);

        Assert.Equal(16, snapshot.Points[127].PhaseA);
        Assert.Equal(6704, snapshot.Points[128].PhaseA);
        Assert.Equal(-7536, snapshot.Points[191].PhaseA);
        Assert.Equal(3328, snapshot.Points[192].PhaseA);
        Assert.Equal(-2208, snapshot.Points[255].PhaseA);
        Assert.Equal(880, snapshot.Points[256].PhaseA);
        Assert.Equal(-576, snapshot.Points[319].PhaseA);
        Assert.Equal(256, snapshot.Points[320].PhaseA);

        Assert.Equal(0, snapshot.Points[127].PhaseB);
        Assert.Equal(5536, snapshot.Points[128].PhaseB);
        Assert.Equal(-6320, snapshot.Points[191].PhaseB);
        Assert.Equal(2848, snapshot.Points[192].PhaseB);
        Assert.Equal(-1856, snapshot.Points[255].PhaseB);
        Assert.Equal(784, snapshot.Points[256].PhaseB);
        Assert.Equal(-528, snapshot.Points[319].PhaseB);
        Assert.Equal(224, snapshot.Points[320].PhaseB);

        WriteAllBlockSamples(snapshot.Blocks);
        WriteAllAlignedPoints(snapshot.Points);
    }

    private static WaveformDebugSnapshot CreateDebugSnapshot()
    {
        var blocks = DocumentedBlockOrder.Select((expected, index) =>
        {
            var frame = PdfWaveformSampleCatalog.Frames[index];
            var samples = frame.Registers.ToArray()
                .Select(WaveformSampleDecoder.DecodeSigned)
                .ToArray();
            return new WaveformDebugBlock(
                expected.ReadOrder,
                expected.SegmentIndex,
                expected.SegmentStartMilliseconds,
                expected.Phase,
                expected.StartAddress,
                samples,
                WaveformSampleDecoder.CalculateRms(samples));
        }).ToArray();

        var phaseA = JoinPhase(blocks, WaveformPhase.A);
        var phaseB = JoinPhase(blocks, WaveformPhase.B);
        var phaseC = JoinPhase(blocks, WaveformPhase.C);
        var points = Enumerable.Range(0, WaveformCatalog.PointsPerPhase)
            .Select(index =>
            {
                var segmentIndex = index / WaveformCatalog.SamplesPerBlock;
                var localIndex = index % WaveformCatalog.SamplesPerBlock;
                return new WaveformDebugPoint(
                    index,
                    WaveformCatalog.GetTimeMilliseconds(index),
                    segmentIndex,
                    localIndex,
                    phaseA[index],
                    phaseB[index],
                    phaseC[index],
                    checked((ushort)(DocumentedBlockOrder[segmentIndex * 3].StartAddress + localIndex)),
                    checked((ushort)(DocumentedBlockOrder[segmentIndex * 3 + 1].StartAddress + localIndex)),
                    checked((ushort)(DocumentedBlockOrder[segmentIndex * 3 + 2].StartAddress + localIndex)));
            }).ToArray();

        return new WaveformDebugSnapshot(blocks, phaseA, phaseB, phaseC, points);
    }

    private static short[] JoinPhase(IEnumerable<WaveformDebugBlock> blocks, WaveformPhase phase) =>
        blocks.Where(block => block.Phase == phase)
            .OrderBy(block => block.SegmentIndex)
            .SelectMany(block => block.Samples)
            .ToArray();

    private void WriteAllBlockSamples(IEnumerable<WaveformDebugBlock> blocks)
    {
        output.WriteLine("================ PDF 18 块完整数据（每块 64 点） ================");
        foreach (var block in blocks)
        {
            output.WriteLine(
                $"[{block.ReadOrder:00}/18] {block.AddressText} {block.Phase} 相 " +
                $"{block.TimeRangeText}; 首={block.First}; 末={block.Last}; " +
                $"范围={block.Minimum}～{block.Maximum}; RMS={block.Rms:F6}");
            for (var offset = 0; offset < block.Samples.Count; offset += 8)
            {
                var values = block.Samples.Skip(offset).Take(8);
                output.WriteLine($"  点 {offset + 1:00}～{offset + 8:00}: {string.Join(", ", values)}");
            }
        }
    }

    private void WriteAllAlignedPoints(IEnumerable<WaveformDebugPoint> points)
    {
        output.WriteLine("================ 三相对齐数据（384 行） ================");
        output.WriteLine("Index | Time(ms) | Seg | Local | A | B | C | AAddr | BAddr | CAddr");
        foreach (var point in points)
        {
            output.WriteLine(
                $"{point.SampleIndex,3} | {point.TimeMilliseconds,8:0.0000} | " +
                $"{point.SegmentIndex,3} | {point.SegmentSampleIndex,5} | " +
                $"{point.PhaseA,6} | {point.PhaseB,6} | {point.PhaseC,3} | " +
                $"{point.PhaseAAddress:X4}H | {point.PhaseBAddress:X4}H | {point.PhaseCAddress:X4}H");
        }
    }

    private sealed record ExpectedBlock(
        int ReadOrder,
        int SegmentIndex,
        double SegmentStartMilliseconds,
        WaveformPhase Phase,
        ushort StartAddress);

    [DebuggerDisplay("{Display,nq}")]
    private sealed record WaveformDebugBlock(
        int ReadOrder,
        int SegmentIndex,
        double SegmentStartMilliseconds,
        WaveformPhase Phase,
        ushort StartAddress,
        IReadOnlyList<short> Samples,
        double Rms)
    {
        public string AddressText => $"{StartAddress:X4}H";
        public string TimeRangeText => $"{SegmentStartMilliseconds:0.####}～{SegmentStartMilliseconds + 20:0.####} ms";
        public short First => Samples[0];
        public short Last => Samples[^1];
        public short Minimum => Samples.Min();
        public short Maximum => Samples.Max();
        private string Display =>
            $"{ReadOrder:00}/18 {AddressText} {Phase} 相 {TimeRangeText}; " +
            $"64 点, {First} -> {Last}, {Minimum}～{Maximum}, RMS={Rms:F6}";
    }

    [DebuggerDisplay("#{SampleIndex} t={TimeMilliseconds}ms A={PhaseA} B={PhaseB} C={PhaseC}")]
    private sealed record WaveformDebugPoint(
        int SampleIndex,
        double TimeMilliseconds,
        int SegmentIndex,
        int SegmentSampleIndex,
        short PhaseA,
        short PhaseB,
        short PhaseC,
        ushort PhaseAAddress,
        ushort PhaseBAddress,
        ushort PhaseCAddress);

    [DebuggerDisplay("18 blocks; A/B/C=384/384/384; Points=384")]
    private sealed record WaveformDebugSnapshot(
        IReadOnlyList<WaveformDebugBlock> Blocks,
        IReadOnlyList<short> PhaseA,
        IReadOnlyList<short> PhaseB,
        IReadOnlyList<short> PhaseC,
        IReadOnlyList<WaveformDebugPoint> Points);
}
