using System.Buffers.Binary;
using WireLink.Core.Communication;

namespace WireLink.Core.Protocol;

/// <summary>
/// 精简且严格的 Modbus RTU 主站。仅实现本项目需要的 03H 和 06H，
/// 每个请求均校验从机地址、功能码、长度和 CRC，并保证总线上一次只有一个请求。
/// </summary>
public sealed class ModbusRtuClient : IModbusRtuClient
{
    private readonly IByteTransport _transport;
    private readonly IProtocolTrace _trace;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private SerialConnectionOptions? _options;

    public ModbusRtuClient(IByteTransport transport, IProtocolTrace? trace = null)
    {
        _transport = transport;
        _trace = trace ?? NullProtocolTrace.Instance;
    }

    public bool IsOpen => _transport.IsOpen;

    public async ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PortName);
        _options = options;
        await _transport.OpenAsync(options, cancellationToken);
        _trace.Information($"传输已打开：{options.PortName}，{options.BaudRate} BPS，8N1");
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        await _transport.CloseAsync(cancellationToken);
        _trace.Information("传输已关闭");
    }

    public Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        if (count is 0 or > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "03H 单次读取数量必须在 1～125 之间。");
        }

        Span<byte> payload = stackalloc byte[6];
        payload[0] = slaveAddress;
        payload[1] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(payload[2..4], startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload[4..6], count);
        return ExecuteWithRetryAsync(
            Crc16Modbus.Append(payload),
            frame => ParseReadResponse(frame, slaveAddress, count),
            cancellationToken);
    }

    public async Task WriteSingleRegisterAsync(
        byte slaveAddress,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        Span<byte> payload = stackalloc byte[6];
        payload[0] = slaveAddress;
        payload[1] = 0x06;
        BinaryPrimitives.WriteUInt16BigEndian(payload[2..4], address);
        BinaryPrimitives.WriteUInt16BigEndian(payload[4..6], value);
        var request = Crc16Modbus.Append(payload);

        await ExecuteWithRetryAsync(
            request,
            frame =>
            {
                if (!frame.AsSpan().SequenceEqual(request))
                {
                    throw new ModbusProtocolException("06H 响应未正确回显写入地址和值。");
                }

                return true;
            },
            cancellationToken);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        byte[] request,
        Func<byte[], T> parser,
        CancellationToken cancellationToken)
    {
        if (!_transport.IsOpen || _options is null)
        {
            throw new InvalidOperationException("串口尚未打开。");
        }

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    await EnforceSilentIntervalAsync(_options.BaudRate, cancellationToken);
                    await _transport.DiscardInputAsync(cancellationToken);
                    _trace.Debug($"TX[{attempt}] {Convert.ToHexString(request)}");
                    await _transport.WriteAsync(request, cancellationToken);
                    var response = await ReadResponseAsync(_options.ReadTimeout, cancellationToken);
                    _trace.Debug($"RX[{attempt}] {Convert.ToHexString(response)}");
                    return parser(response);
                }
                catch (Exception ex) when (attempt == 1 && ex is TimeoutException or ModbusCrcException)
                {
                    lastError = ex;
                    _trace.Warning($"请求第 1 次失败，将重试：{ex.Message}");
                }
            }

            throw lastError ?? new ModbusProtocolException("Modbus 请求失败。");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<byte[]> ReadResponseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var header = new byte[3];
            await ReadExactlyAsync(header.AsMemory(0, 2), timeoutSource.Token);
            var isException = (header[1] & 0x80) != 0;
            if (isException)
            {
                var tail = new byte[3];
                await ReadExactlyAsync(tail, timeoutSource.Token);
                var exceptionFrame = new[] { header[0], header[1], tail[0], tail[1], tail[2] };
                ValidateCrc(exceptionFrame);
                throw new ModbusDeviceException(tail[0]);
            }

            await ReadExactlyAsync(header.AsMemory(2, 1), timeoutSource.Token);
            var tailLength = header[2] + 2;
            var tailBytes = new byte[tailLength];
            await ReadExactlyAsync(tailBytes, timeoutSource.Token);
            var frame = new byte[header.Length + tailBytes.Length];
            header.CopyTo(frame, 0);
            tailBytes.CopyTo(frame, header.Length);
            ValidateCrc(frame);
            return frame;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"设备在 {timeout.TotalMilliseconds:0} ms 内未返回完整响应。");
        }
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await _transport.ReadAsync(buffer[offset..], cancellationToken);
            if (count <= 0)
            {
                throw new EndOfStreamException("串口在响应完成前已关闭。");
            }

            offset += count;
        }
    }

    private static ushort[] ParseReadResponse(byte[] frame, byte slaveAddress, ushort count)
    {
        if (frame[0] != slaveAddress)
        {
            throw new ModbusProtocolException($"响应从机地址不匹配：期望 {slaveAddress}，收到 {frame[0]}。");
        }

        if (frame[1] != 0x03)
        {
            throw new ModbusProtocolException($"响应功能码不匹配：期望 03H，收到 {frame[1]:X2}H。");
        }

        var expectedBytes = count * 2;
        if (frame[2] != expectedBytes || frame.Length != expectedBytes + 5)
        {
            throw new ModbusProtocolException("03H 响应字节数与请求寄存器数量不一致。");
        }

        var values = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3 + index * 2, 2));
        }

        return values;
    }

    private static void ValidateCrc(ReadOnlySpan<byte> frame)
    {
        if (!Crc16Modbus.IsValid(frame))
        {
            throw new ModbusCrcException($"响应 CRC 校验失败：{Convert.ToHexString(frame)}");
        }
    }

    private static Task EnforceSilentIntervalAsync(int baudRate, CancellationToken cancellationToken)
    {
        // 8N1 每字符约 10 bit。RTU 帧间隔至少 3.5 字符，并设置 2 ms 下限以兼容高速 USB 转换器。
        var milliseconds = Math.Max(2, (int)Math.Ceiling(3.5 * 10_000d / baudRate));
        return Task.Delay(milliseconds, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        _requestLock.Dispose();
    }
}
