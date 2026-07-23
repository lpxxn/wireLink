using System.IO.Ports;
using WireLink.Core.Communication;

namespace WireLink.Infrastructure.Serial;

/// <summary>基于 System.IO.Ports 的 8N1、无流控字节传输实现。</summary>
public sealed class SerialPortTransport : IByteTransport
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private SerialPort? _port;

    public bool IsOpen => _port?.IsOpen == true;

    public async ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (IsOpen) return;
            var port = new SerialPort(options.PortName, options.BaudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false,
                // SerialPort.BaseStream.ReadAsync does not reliably observe cancellation on
                // Windows. Keep the driver operations bounded so a silent device cannot leave
                // the UI permanently waiting for a request to finish.
                ReadTimeout = ToSerialTimeout(options.ReadTimeout),
                WriteTimeout = ToSerialTimeout(options.WriteTimeout),
            };
            port.Open();
            _port = port;
        }
        finally { _lifecycleLock.Release(); }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var port = _port;
            _port = null;
            if (port is null) return;
            try { if (port.IsOpen) port.Close(); }
            finally { port.Dispose(); }
        }
        finally { _lifecycleLock.Release(); }
    }

    public ValueTask DiscardInputAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetOpenPort().DiscardInBuffer();
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = GetOpenPort();
        var bytes = data.ToArray();
        await Task.Run(() => port.Write(bytes, 0, bytes.Length), cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = GetOpenPort();
        var bytes = new byte[buffer.Length];
        var count = await Task.Run(() => port.Read(bytes, 0, bytes.Length), cancellationToken);
        bytes.AsMemory(0, count).CopyTo(buffer);
        return count;
    }

    private static int ToSerialTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout), "串口超时必须大于 0 且不超过 Int32 毫秒上限。");

        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private SerialPort GetOpenPort() => _port is { IsOpen: true } port
        ? port
        : throw new InvalidOperationException("串口尚未打开或已断开。");

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _lifecycleLock.Dispose();
    }
}

public sealed class SerialPortCatalog : ISerialPortCatalog
{
    public IReadOnlyList<string> GetPortNames() => SerialPort.GetPortNames()
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
