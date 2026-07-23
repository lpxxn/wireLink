using System.Text.Json;
using System.Text.Json.Serialization;
using WireLink.Core.Services;

namespace WireLink.Infrastructure.Settings;

/// <summary>将用户偏好保存到系统应用数据目录；损坏文件会回退默认值。</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonSettingsService(string? path = null)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WireLink");
        _path = path ?? Path.Combine(directory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                .ConfigureAwait(false) ?? new AppSettings();
        }
        catch (JsonException) { return new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken)
            .ConfigureAwait(false);
    }
}
