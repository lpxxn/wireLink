using WireLink.Core.Communication;
using WireLink.Core.Models;
using WireLink.Core.Registers;

namespace WireLink.Core.Services;

/// <summary>设备数据读取服务。每个不连续区间独立读取，允许部分成功。</summary>
public interface IDeviceDataService
{
    Task<bool> TestConnectionAsync(byte slaveAddress, CancellationToken cancellationToken = default);
    Task<DataReadResult> ReadAsync(byte slaveAddress, WordOrder wordOrder, CancellationToken cancellationToken = default);
}

/// <summary>历史故障记录读取服务。</summary>
public interface IFaultRecordService
{
    Task<DataReadResult> ReadAsync(byte slaveAddress, FaultRecordType type, byte recordIndex,
        WordOrder wordOrder, TimeSpan readyDelay, CancellationToken cancellationToken = default);
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
    int FaultReadyDelayMilliseconds = 100);

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

public interface IExcelExportService
{
    Task ExportAsync(string path, ExcelExportContext context, CancellationToken cancellationToken = default);
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
        CancellationToken cancellationToken = default)
    {
        var samples = new Dictionary<ushort, RawRegisterSample>();
        var errors = new List<string>();
        var readAt = DateTimeOffset.Now;

        // 先读取隐藏的电流变比，使随后成功的电流区间可以立即计算。
        var blocks = RegisterCatalog.DeviceBlocks.OrderByDescending(block => block.StartAddress == 788);
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

        return new DataReadResult(parser.Parse(RegisterCatalog.DeviceDefinitions, samples, wordOrder), errors, readAt);
    }
}

public sealed class FaultRecordService(IModbusRtuClient client, RegisterParser parser) : IFaultRecordService
{
    public async Task<DataReadResult> ReadAsync(byte slaveAddress, FaultRecordType type, byte recordIndex,
        WordOrder wordOrder, TimeSpan readyDelay, CancellationToken cancellationToken = default)
    {
        if (recordIndex > 15) throw new ArgumentOutOfRangeException(nameof(recordIndex), "记录序号必须为 0～15。");
        var selector = (ushort)((recordIndex << 8) | (byte)type);
        await client.WriteSingleRegisterAsync(slaveAddress, 785, selector, cancellationToken);
        if (readyDelay > TimeSpan.Zero) await Task.Delay(readyDelay, cancellationToken);

        var readAt = DateTimeOffset.Now;
        try
        {
            var raw = await client.ReadHoldingRegistersAsync(slaveAddress, 768, 21, cancellationToken);
            var samples = raw.Select((value, index) =>
            {
                var address = checked((ushort)(768 + index));
                return new RawRegisterSample(address, value, readAt);
            }).ToDictionary(sample => sample.Address);
            return new DataReadResult(parser.Parse(RegisterCatalog.FaultDefinitions, samples, wordOrder, type), [], readAt);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DataReadResult([], [$"读取故障记录失败：{ex.Message}"], readAt);
        }
    }
}
