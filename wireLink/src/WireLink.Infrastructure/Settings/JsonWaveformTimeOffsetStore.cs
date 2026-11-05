using System.Security.Cryptography;
using System.Text.Json;
using WireLink.Core.Communication;
using WireLink.Core.Registers;
using WireLink.Core.Services;

namespace WireLink.Infrastructure.Settings;

/// <summary>
/// 把秒级故障时间映射到软件补充毫秒。键只包含 yyyy-MM-dd HH:mm:ss，
/// 因而不同设备或记录只要故障时间相同，就会复用同一个毫秒值。
/// </summary>
public sealed class JsonWaveformTimeOffsetStore : IWaveformTimeOffsetStore
{
    private readonly string _path;
    private readonly IProtocolTrace _trace;
    private readonly Func<int> _generateMilliseconds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, int>? _offsets;

    public JsonWaveformTimeOffsetStore(
        string? path = null,
        IProtocolTrace? trace = null,
        Func<int>? generateMilliseconds = null)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WireLink");
        _path = path ?? Path.Combine(directory, "waveform-time-offsets.json");
        _trace = trace ?? NullProtocolTrace.Instance;
        _generateMilliseconds = generateMilliseconds ?? (() => RandomNumberGenerator.GetInt32(0, 1000));
    }

    public async Task<int> GetOrCreateAsync(
        DateTime faultRecordTime,
        CancellationToken cancellationToken = default)
    {
        var key = FaultRecordTimeDecoder.Format(faultRecordTime);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _offsets ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_offsets.TryGetValue(key, out var existing))
                return existing;

            var created = _generateMilliseconds();
            if (created is < 0 or > 999)
                throw new InvalidOperationException($"软件补充毫秒生成器返回了非法值 {created}，必须为 0～999。");

            _offsets[key] = created;
            try
            {
                await SaveAsync(_offsets, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 保存失败时不能让仅存在于内存中的值成为“稳定时间”，撤销本次新增。
                _offsets.Remove(key);
                throw;
            }

            _trace.Information($"录波时间补充毫秒已生成并保存；故障时间={key}；补充毫秒={created}");
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, int>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            await using var stream = File.OpenRead(_path);
            var values = await JsonSerializer.DeserializeAsync<Dictionary<string, int>>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, value) in values ?? [])
            {
                if (value is >= 0 and <= 999)
                    result[key] = value;
                else
                    _trace.Warning($"忽略非法录波补充毫秒；故障时间={key}；值={value}");
            }
            return result;
        }
        catch (JsonException ex)
        {
            _trace.Warning($"录波时间补充毫秒文件损坏，将重新建立：{ex.Message}");
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private async Task SaveAsync(
        IReadOnlyDictionary<string, int> values,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    values,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
