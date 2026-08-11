using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;

namespace WireLink.Core.Services;

/// <summary>设备数据读取服务。每个不连续区间独立读取，允许部分成功。</summary>
public interface IDeviceDataService
{
    Task<bool> TestConnectionAsync(byte slaveAddress, CancellationToken cancellationToken = default);
    Task<DataReadResult> ReadAsync(byte slaveAddress, WordOrder wordOrder, BreakerSeries controllerSeries,
        CancellationToken cancellationToken = default);
}

/// <summary>历史故障记录读取服务。</summary>
public interface IFaultRecordService
{
    Task<DataReadResult> ReadAsync(byte slaveAddress, FaultRecordType type, byte recordIndex,
        WordOrder wordOrder, BreakerSeries controllerSeries, TimeSpan readyDelay,
        CancellationToken cancellationToken = default);
}

/// <summary>固定录波区读取服务。只有 18 个块全部成功时才返回完整数据。</summary>
public interface IWaveformDataService
{
    Task<WaveformData> ReadAsync(
        byte slaveAddress,
        IProgress<WaveformReadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum AppThemeMode { System, Light, Dark }

/// <summary>可持久化设置。启动恢复字段，但永远不保存“已连接”状态。</summary>
public sealed record AppSettings(
    string PortName = "",
    int BaudRate = 9600,
    byte DeviceAddress = 1,
    int RefreshSeconds = 3,
    AppThemeMode Theme = AppThemeMode.System,
    WordOrder WordOrder = WordOrder.HighWordFirst,
    int ReadTimeoutMilliseconds = 1000,
    int FaultReadyDelayMilliseconds = 100,
    BreakerSeries ControllerSeries = BreakerSeries.BW1);

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed record ExcelExportContext(
    string Title,
    IReadOnlyList<DecodedValue> Values,
    DateTimeOffset ReadAt,
    FaultRecordType? RecordType = null,
    byte? RecordIndex = null);

public sealed record WaveformExcelExportContext(string Title, WaveformData Data);

/// <summary>录波原始点明细窗口专用导出上下文；导出的列和窗口中的 16 列表格完全一致。</summary>
public sealed record WaveformPointDetailsExcelExportContext(string Title, WaveformData Data);

public interface IExcelExportService
{
    Task ExportAsync(string path, ExcelExportContext context, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, WaveformExcelExportContext context, CancellationToken cancellationToken = default);
    Task ExportAsync(string path, WaveformPointDetailsExcelExportContext context, CancellationToken cancellationToken = default);
}

public enum LogLevel { Debug, Information, Warning, Error }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message, Exception? Exception = null);

public interface ILogStore
{
    IReadOnlyList<LogEntry> Snapshot { get; }
    event EventHandler<LogEntry>? EntryAdded;
    void Add(LogEntry entry);
    void ClearDisplay();
    string LogDirectory { get; }
}

public sealed class DeviceDataService(IModbusRtuClient client, RegisterParser parser, IProtocolTrace? trace = null)
    : IDeviceDataService
{
    private readonly IProtocolTrace _trace = trace ?? NullProtocolTrace.Instance;

    public async Task<bool> TestConnectionAsync(byte slaveAddress, CancellationToken cancellationToken = default)
    {
        var values = await client.ReadHoldingRegistersAsync(slaveAddress, 256, 1, cancellationToken);
        return values.Length == 1;
    }

    public async Task<DataReadResult> ReadAsync(byte slaveAddress, WordOrder wordOrder,
        BreakerSeries controllerSeries,
        CancellationToken cancellationToken = default)
    {
        var samples = new Dictionary<ushort, RawRegisterSample>();
        var errors = new List<string>();
        var readAt = DateTimeOffset.Now;

        // 先读取隐藏的额定电流配置，使随后成功的电流区间可以立即使用 1552.bit0～bit7 计算。
        var blocks = RegisterCatalog.DeviceBlocks.OrderByDescending(
            block => block.StartAddress == RegisterCatalog.RatedCurrentRegisterAddress);
        foreach (var block in blocks)
        {
            try
            {
                var values = await client.ReadHoldingRegistersAsync(slaveAddress, block.StartAddress, block.Count, cancellationToken);
                var timestamp = DateTimeOffset.Now;
                for (var index = 0; index < values.Length; index++)
                {
                    var address = checked((ushort)(block.StartAddress + index));
                    samples[address] = new RawRegisterSample(address, values[index], timestamp);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var error = $"读取 {block.StartAddress}～{block.EndAddress} 失败：{ex.Message}";
                errors.Add(error);
                _trace.Warning(error);
            }
        }

        return new DataReadResult(
            parser.Parse(RegisterCatalog.DeviceDefinitions, samples, wordOrder, controllerSeries: controllerSeries),
            errors,
            readAt);
    }
}

public sealed class FaultRecordService(IModbusRtuClient client, RegisterParser parser) : IFaultRecordService
{
    public async Task<DataReadResult> ReadAsync(byte slaveAddress, FaultRecordType type, byte recordIndex,
        WordOrder wordOrder, BreakerSeries controllerSeries, TimeSpan readyDelay,
        CancellationToken cancellationToken = default)
    {
        if (recordIndex > 15) throw new ArgumentOutOfRangeException(nameof(recordIndex), "第几条记录必须为 0～15。");
        var selector = (ushort)((recordIndex << 8) | (byte)type);
        await client.WriteSingleRegisterAsync(slaveAddress, 785, selector, cancellationToken);
        if (readyDelay > TimeSpan.Zero) await Task.Delay(readyDelay, cancellationToken);

        var readAt = DateTimeOffset.Now;
        var samples = new Dictionary<ushort, RawRegisterSample>();
        var errors = new List<string>();

        try
        {
            var raw = await client.ReadHoldingRegistersAsync(slaveAddress, 768, 19, cancellationToken);
            foreach (var (value, index) in raw.Select((value, index) => (value, index)))
            {
                var address = checked((ushort)(768 + index));
                samples[address] = new RawRegisterSample(address, value, readAt);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"读取故障记录 768～786 失败：{ex.Message}");
        }

        try
        {
            var ratedCurrentConfiguration = await client.ReadHoldingRegistersAsync(
                slaveAddress, RegisterCatalog.RatedCurrentRegisterAddress, 1, cancellationToken);
            samples[RegisterCatalog.RatedCurrentRegisterAddress] = new RawRegisterSample(
                RegisterCatalog.RatedCurrentRegisterAddress,
                ratedCurrentConfiguration[0],
                DateTimeOffset.Now);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"读取额定电流配置 1552 失败：{ex.Message}");
        }

        try
        {
            var operationCount = await client.ReadHoldingRegistersAsync(
                slaveAddress, 1031, 1, cancellationToken);
            samples[1031] = new RawRegisterSample(1031, operationCount[0], DateTimeOffset.Now);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"读取总操作次数 1031 失败：{ex.Message}");
        }

        return new DataReadResult(
            parser.Parse(RegisterCatalog.FaultDefinitions, samples, wordOrder, type, controllerSeries),
            errors,
            readAt);
    }
}

/// <summary>
/// 按协议规定的 18 个块依次读取三相录波。任一块失败即终止，避免把不同批次或残缺数据拼接。
/// </summary>
public sealed class WaveformDataService(IModbusRtuClient client, IProtocolTrace? trace = null)
    : IWaveformDataService
{
    private readonly IProtocolTrace _trace = trace ?? NullProtocolTrace.Instance;

    public async Task<WaveformData> ReadAsync(
        byte slaveAddress,
        IProgress<WaveformReadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var readAt = DateTimeOffset.Now;
        var phaseValues = Enum.GetValues<WaveformPhase>()
            .ToDictionary(phase => phase, _ => new short[WaveformCatalog.PointsPerPhase]);
        var completed = 0;

        foreach (var block in WaveformCatalog.Blocks)
        {
            try
            {
                var raw = await client.ReadHoldingRegistersAsync(
                    slaveAddress, block.StartAddress, block.Count, cancellationToken);

                if (raw.Length != block.Count)
                    throw new ModbusProtocolException(
                        $"录波块返回数量错误：期望 {block.Count}，收到 {raw.Length}。");

                var samples = WaveformSampleDecoder.DecodeSignedSamples(raw);
                var destination = phaseValues[block.Phase];
                var destinationIndex = block.SegmentIndex * WaveformCatalog.SamplesPerBlock;
                samples.CopyTo(destination, destinationIndex);

                completed++;
                progress?.Report(new WaveformReadProgress(completed, WaveformCatalog.TotalBlocks, block));
                _trace.Debug(
                    $"录波块读取成功；相别={block.Phase}；时间={block.TimeRangeText}；" +
                    $"地址={block.StartAddress}～{block.EndAddress} " +
                    $"(0x{block.StartAddress:X4}～0x{block.EndAddress:X4})；" +
                    $"点数={samples.Length}；首值={samples[0]}；末值={samples[^1]}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message =
                    $"读取录波块失败：{block.Phase} 相，{block.TimeRangeText}，" +
                    $"地址 {block.StartAddress}～{block.EndAddress} " +
                    $"(0x{block.StartAddress:X4}～0x{block.EndAddress:X4})：{ex.Message}";
                _trace.Error(message, ex);
                throw new InvalidOperationException(message, ex);
            }
        }

        var points = new WaveformPoint[WaveformCatalog.PointsPerPhase];
        for (var sampleIndex = 0; sampleIndex < points.Length; sampleIndex++)
        {
            var segmentIndex = sampleIndex / WaveformCatalog.SamplesPerBlock;
            var segmentSampleIndex = sampleIndex % WaveformCatalog.SamplesPerBlock;
            var aBlock = WaveformCatalog.GetBlock(segmentIndex, WaveformPhase.A);
            var bBlock = WaveformCatalog.GetBlock(segmentIndex, WaveformPhase.B);
            var cBlock = WaveformCatalog.GetBlock(segmentIndex, WaveformPhase.C);

            points[sampleIndex] = new WaveformPoint(
                sampleIndex,
                segmentIndex,
                segmentSampleIndex,
                WaveformCatalog.GetTimeMilliseconds(sampleIndex),
                phaseValues[WaveformPhase.A][sampleIndex],
                phaseValues[WaveformPhase.B][sampleIndex],
                phaseValues[WaveformPhase.C][sampleIndex],
                checked((ushort)(aBlock.StartAddress + segmentSampleIndex)),
                checked((ushort)(bBlock.StartAddress + segmentSampleIndex)),
                checked((ushort)(cBlock.StartAddress + segmentSampleIndex)));
        }

        return new WaveformData(
            readAt,
            WaveformCatalog.SampleRateHz,
            points,
            WaveformSampleDecoder.CalculateRms(phaseValues[WaveformPhase.A]),
            WaveformSampleDecoder.CalculateRms(phaseValues[WaveformPhase.B]),
            WaveformSampleDecoder.CalculateRms(phaseValues[WaveformPhase.C]));
    }
}
