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
                ReadTimeout = Timeout.Infinite,
                WriteTimeout = Timeout.Infinite,
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
        var stream = GetOpenPort().BaseStream;
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        GetOpenPort().BaseStream.ReadAsync(buffer, cancellationToken);

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
