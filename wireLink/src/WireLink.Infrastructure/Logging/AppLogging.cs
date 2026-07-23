using Serilog;
using Serilog.Core;
using Serilog.Events;
using WireLink.Core.Communication;
using WireLink.Core.Services;

namespace WireLink.Infrastructure.Logging;

public sealed class InMemoryLogStore(string logDirectory, int capacity = 10_000) : ILogStore
{
    private readonly object _sync = new();
    private readonly Queue<LogEntry> _entries = new(capacity);
    public string LogDirectory { get; } = logDirectory;
    public event EventHandler<LogEntry>? EntryAdded;
    public IReadOnlyList<LogEntry> Snapshot { get { lock (_sync) return _entries.ToArray(); } }

    public void Add(LogEntry entry)
    {
        lock (_sync)
        {
            while (_entries.Count >= capacity) _entries.Dequeue();
            _entries.Enqueue(entry);
        }
        EntryAdded?.Invoke(this, entry);
    }

    public void ClearDisplay() { lock (_sync) _entries.Clear(); }
}

public sealed class LogStoreSink(ILogStore store) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Debug or LogEventLevel.Verbose => LogLevel.Debug,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error or LogEventLevel.Fatal => LogLevel.Error,
            _ => LogLevel.Information,
        };
        store.Add(new LogEntry(logEvent.Timestamp, level, logEvent.RenderMessage(), logEvent.Exception));
    }
}

public sealed class SerilogProtocolTrace(ILogger logger) : IProtocolTrace
{
    public void Debug(string message) => logger.Debug("{ProtocolMessage}", message);
    public void Information(string message) => logger.Information("{ProtocolMessage}", message);
    public void Warning(string message) => logger.Warning("{ProtocolMessage}", message);
    public void Error(string message, Exception? exception = null) => logger.Error(exception, "{ProtocolMessage}", message);
}

public static class AppLogging
{
    public static (ILogger Logger, InMemoryLogStore Store) Create()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WireLink", "logs");
        Directory.CreateDirectory(directory);
        var store = new InMemoryLogStore(directory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new LogStoreSink(store))
            .WriteTo.File(Path.Combine(directory, "wirelink-.log"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return (logger, store);
    }
}
