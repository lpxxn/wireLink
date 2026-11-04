using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Registers;
using WireLink.Core.Services;

namespace WireLink.Tests;

public sealed class WaveformBlockRmsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-32768, 32768)]
    [InlineData(32767, 32767)]
    public void Block_rms_handles_int16_boundaries_and_owns_immutable_copy(short sample, double expected)
    {
        var values = Enumerable.Repeat(sample, 64).ToArray();
        var calibration = WaveformCalibration.FromRegisterValue(0x0204);
        var block = WaveformBlockResult.Create(WaveformCatalog.Blocks[0], values, calibration);
        values[0] = 123;
        Assert.Equal(sample, block.Samples[0]);
        Assert.Equal(expected, block.AdRms);
        Assert.Equal(WaveformCalibration.RoundAmperes(expected * 20000.0 / 22953.0), block.AmperesRms);
        Assert.Throws<NotSupportedException>(() => ((IList<short>)block.Samples)[0] = 2);
    }

    [Theory]
    [InlineData(0x0000, 1.0)]
    [InlineData(0x0100, 1.5)]
    [InlineData(0x0200, 2.0)]
    public void Rms_uses_unrounded_signed_ad_before_applying_rate(int register, double rate)
    {
        var samples = Enumerable.Range(0, 64).Select(index => (short)(index % 2 == 0 ? 1 : -1));
        var block = WaveformBlockResult.Create(WaveformCatalog.Blocks[0], samples,
            WaveformCalibration.FromRegisterValue((ushort)register));
        Assert.Equal(1, block.AdRms);
        Assert.Equal(WaveformCalibration.RoundAmperes(10000.0 / 22953.0 * rate), block.AmperesRms);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public void Partial_or_oversized_block_is_rejected(int count) =>
        Assert.Throws<ArgumentException>(() => WaveformBlockResult.Create(WaveformCatalog.Blocks[0],
            new short[count], WaveformCalibration.FromRegisterValue(0x0204)));

    [Fact]
    public async Task First_block_rms_is_published_while_second_request_is_still_blocked()
    {
        await using var client = new BlockingClient();
        var updates = new List<WaveformReadProgress>();
        var read = new WaveformDataService(client).ReadAsync(4, new InlineProgress(updates.Add));
        await client.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = Assert.Single(updates);
        Assert.False(read.IsCompleted);
        Assert.Equal(1, first.CompletedBlocks);
        Assert.Equal(32768, first.BlockResult.AdRms);
        Assert.Null(first.CompletedSegment);
        client.ReleaseSecondRequest.SetResult();
        var data = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(18, updates.Count);
        Assert.Equal(6, data.Segments.Count);
        Assert.All(updates, update => Assert.Equal(update.CompletedBlocks % 3 == 0, update.CompletedSegment is not null));
        Assert.Same(updates[2].CompletedSegment, data.Segments[0]);
        Assert.Equal(short.MinValue, data.Segments[0].Points[0].PhaseA);
        Assert.Throws<NotSupportedException>(() => ((IList<WaveformPoint>)data.Segments[0].Points).Clear());
    }

    private sealed class InlineProgress(Action<WaveformReadProgress> callback) : IProgress<WaveformReadProgress>
    {
        public void Report(WaveformReadProgress value) => callback(value);
    }

    private sealed class BlockingClient : IModbusRtuClient
    {
        public TaskCompletionSource SecondRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsOpen => true;
        public ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task WriteSingleRegisterAsync(byte slaveAddress, ushort address, ushort value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort count, CancellationToken cancellationToken = default)
        {
            if (startAddress == 1552) return [0x0204];
            if (startAddress == 0xB040)
            {
                SecondRequestStarted.SetResult();
                await ReleaseSecondRequest.Task.WaitAsync(cancellationToken);
            }
            return Enumerable.Repeat((ushort)0x8000, count).ToArray();
        }
    }
}
