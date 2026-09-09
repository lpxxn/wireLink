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

public sealed class ConsoleLogSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        try
        {
            var writer = logEvent.Level >= LogEventLevel.Error ? Console.Error : Console.Out;
            writer.WriteLine($"{logEvent.Timestamp:HH:mm:ss.fff} [{logEvent.Level.ToString()[..3].ToUpperInvariant()}] {logEvent.RenderMessage()}");
            if (logEvent.Exception is { } exception)
                writer.WriteLine(exception);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}

public static class AppLogging
{
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WireLink", "logs");

    public static void WriteCrash(Exception exception)
    {
        // 最后的崩溃兜底路径，任何记录失败都不能覆盖原始异常。
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                Path.Combine(LogDirectory, "crash.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}" +
                $"{Environment.NewLine}{exception}" +
                $"{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 文件目录无权限、路径非法、磁盘故障等情况下继续尝试控制台。
        }

        try
        {
            Console.Error.WriteLine(exception);
        }
        catch
        {
            // Release/WinExe 没有控制台或输出流已关闭时忽略。
        }
    }

    public static (ILogger Logger, InMemoryLogStore Store) Create()
    {
        Directory.CreateDirectory(LogDirectory);
        var store = new InMemoryLogStore(LogDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new LogStoreSink(store))
            .WriteTo.Sink(new ConsoleLogSink())
            .WriteTo.File(Path.Combine(LogDirectory, "wirelink-.log"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return (logger, store);
    }
}
