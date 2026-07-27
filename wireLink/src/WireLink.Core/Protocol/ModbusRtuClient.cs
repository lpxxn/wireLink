using System.Buffers.Binary;
using WireLink.Core.Communication;

namespace WireLink.Core.Protocol;

/// <summary>
/// 基于字节传输层实现的 Modbus RTU 主站客户端。
/// </summary>
/// <remarks>
/// <para>
/// 当前只实现项目所需的 03H“读保持寄存器”和 06H“写单个保持寄存器”。
/// 请求帧由本类编码，CRC 由 <see cref="Crc16Modbus"/> 追加；响应会校验 CRC、从机地址、
/// 功能码、字节数以及 06H 回显内容。
/// </para>
/// <para>
/// RS485 是半双工总线，同一时刻只能存在一个请求。本类使用
/// <see cref="SemaphoreSlim"/> 将手动读取、自动刷新和故障读取串行化，调用方不需要另外加锁。
/// 锁的范围覆盖静默间隔、清理旧输入、发送、接收和解析全过程。
/// </para>
/// <para>
/// 超时和 CRC 错误可能由临时干扰造成，因此首次失败后自动重试一次。
/// 设备异常响应、地址/功能码/长度不匹配以及串口断开不会自动重试，避免对确定性错误重复发送命令。
/// 调用方传入的取消请求也不会被转换为超时或触发重试。
/// </para>
/// <para>
/// 本类不直接依赖 <c>System.IO.Ports.SerialPort</c>，而是依赖
/// <see cref="IByteTransport"/>。正式程序可使用真实串口实现，测试和模拟环境可使用内存传输实现。
/// </para>
/// </remarks>
public sealed class ModbusRtuClient : IModbusRtuClient
{
    /// <summary>负责实际打开端口以及读写原始字节的传输层。</summary>
    private readonly IByteTransport _transport;

    /// <summary>记录收发帧、寄存器地址、重试和失败原因的协议日志。</summary>
    private readonly IProtocolTrace _trace;

    /// <summary>
    /// 全局请求互斥锁。初始计数和最大计数均为 1，保证 RS485 总线上一次只有一个请求。
    /// </summary>
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    /// <summary>
    /// 最近一次成功打开时使用的串口参数。读取响应时需要其中的波特率和超时时间。
    /// </summary>
    private SerialConnectionOptions? _options;

    /// <summary>
    /// 创建 Modbus RTU 客户端。
    /// </summary>
    /// <param name="transport">提供原始字节收发能力的传输层，不能为空。</param>
    /// <param name="trace">
    /// 可选协议日志记录器；未传入时使用空实现，不影响通信流程。
    /// </param>
    public ModbusRtuClient(IByteTransport transport, IProtocolTrace? trace = null)
    {
        _transport = transport;
        _trace = trace ?? NullProtocolTrace.Instance;
    }

    /// <summary>获取底层串口或字节传输是否已经打开。</summary>
    public bool IsOpen => _transport.IsOpen;

    /// <summary>
    /// 使用指定参数打开底层传输。
    /// </summary>
    /// <param name="options">端口、波特率以及读写超时参数。</param>
    /// <param name="cancellationToken">用于取消打开操作的令牌。</param>
    /// <returns>表示异步打开过程的任务。</returns>
    /// <exception cref="ArgumentException">端口名称为空或只包含空白字符。</exception>
    public async ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PortName);

        // 保存参数供后续静默间隔和响应超时计算使用。
        _options = options;
        await _transport.OpenAsync(options, cancellationToken);
        _trace.Information($"传输已打开：{options.PortName}，{options.BaudRate} BPS，8N1");
    }

    /// <summary>
    /// 关闭底层传输。关闭端口不会自动清除客户端对象，之后仍可再次调用
    /// <see cref="OpenAsync"/> 重新打开。
    /// </summary>
    /// <param name="cancellationToken">用于取消关闭操作的令牌。</param>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        await _transport.CloseAsync(cancellationToken);
        _trace.Information("传输已关闭");
    }

    /// <summary>
    /// 使用 03H 功能码读取一个或多个连续保持寄存器。
    /// </summary>
    /// <param name="slaveAddress">Modbus 从机地址；项目的读操作不使用广播地址 0。</param>
    /// <param name="startAddress">第一个寄存器的十进制地址。</param>
    /// <param name="count">连续读取的寄存器数量，Modbus 03H 允许 1～125。</param>
    /// <param name="cancellationToken">用于取消排队、发送或接收的令牌。</param>
    /// <returns>
    /// 按地址从低到高排列的 16 位原始寄存器数组。每个寄存器在帧内按高字节、低字节解码。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 不在 1～125 范围内。</exception>
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

        // 03H 请求的 CRC 前内容固定为 6 字节：
        // [从机地址][03][起始地址高][起始地址低][数量高][数量低]。
        Span<byte> payload = stackalloc byte[6];
        payload[0] = slaveAddress;
        payload[1] = 0x03;
        // Modbus 多字节数值均使用网络字节序，即高字节在前。
        BinaryPrimitives.WriteUInt16BigEndian(payload[2..4], startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(payload[4..6], count);

        // Append 在末尾按“CRC 低字节、CRC 高字节”追加两个字节，形成完整 8 字节请求。
        return ExecuteWithRetryAsync(
            Crc16Modbus.Append(payload),
            frame => ParseReadResponse(frame, slaveAddress, count),
            cancellationToken);
    }

    /// <summary>
    /// 使用 06H 功能码写入单个保持寄存器。
    /// </summary>
    /// <param name="slaveAddress">目标 Modbus 从机地址。</param>
    /// <param name="address">需要写入的寄存器地址。</param>
    /// <param name="value">写入的 16 位原始值。</param>
    /// <param name="cancellationToken">用于取消排队、发送或接收的令牌。</param>
    /// <remarks>
    /// 正常 06H 响应必须逐字节回显请求帧，包括从机地址、功能码、寄存器地址、写入值和 CRC。
    /// 本方法只有在完整回显一致后才认为写入成功。
    /// </remarks>
    public async Task WriteSingleRegisterAsync(
        byte slaveAddress,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        // 06H 请求的 CRC 前内容固定为 6 字节：
        // [从机地址][06][寄存器地址高][寄存器地址低][写入值高][写入值低]。
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
                // 06H 的成功响应不是独立数据结构，而是完整回显原请求。
                if (!frame.AsSpan().SequenceEqual(request))
                {
                    throw new ModbusProtocolException("06H 响应未正确回显写入地址和值。");
                }

                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// 串行执行一个完整 Modbus 请求，并按项目规则处理日志、超时和一次重试。
    /// </summary>
    /// <typeparam name="T">响应解析后的业务结果类型。</typeparam>
    /// <param name="request">已包含 CRC 的完整 RTU 请求帧。</param>
    /// <param name="parser">
    /// 响应解析函数。CRC 在调用解析函数前已经校验；解析函数继续校验业务相关的地址、功能码和长度。
    /// </param>
    /// <param name="cancellationToken">用于取消等待请求锁以及实际收发过程的令牌。</param>
    /// <returns>由 <paramref name="parser"/> 生成的业务结果。</returns>
    private async Task<T> ExecuteWithRetryAsync<T>(
        byte[] request,
        Func<byte[], T> parser,
        CancellationToken cancellationToken)
    {
        if (!_transport.IsOpen || _options is null)
        {
            throw new InvalidOperationException("串口尚未打开。");
        }

        // 等待互斥锁期间也支持取消。获取锁之后直到 finally 释放前，
        // 其他手动读取、自动刷新或故障读取请求都只能排队等待。
        await _requestLock.WaitAsync(cancellationToken);

        // 在真正发送前解析一次请求摘要，保证 TX、RX、重试和失败日志使用完全一致的地址信息。
        var requestContext = DescribeRequest(request);
        try
        {
            Exception? lastError = null;

            // 最多尝试两次：第一次正常请求，只有超时或 CRC 错误才进入第二次。
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    // RTU 使用帧间静默时间划分报文。发送新帧前先满足至少 3.5 个字符时间。
                    await EnforceSilentIntervalAsync(_options.BaudRate, cancellationToken);

                    // 丢弃上一个超时或线路噪声遗留的字节，防止旧数据被误认为本次响应。
                    await _transport.DiscardInputAsync(cancellationToken);
                    _trace.Debug($"TX[{attempt}]；{requestContext}；帧={Convert.ToHexString(request)}");
                    await _transport.WriteAsync(request, cancellationToken);

                    // ReadResponseAsync 负责按响应帧结构分段接收，并将内部超时转换为 TimeoutException。
                    var response = await ReadResponseAsync(_options.ReadTimeout, cancellationToken);
                    _trace.Debug($"RX[{attempt}]；{requestContext}；帧={Convert.ToHexString(response)}");

                    // CRC 正确只表示帧未损坏；地址、功能码、长度或回显内容由具体 parser 继续验证。
                    var result = parser(response);
                    _trace.Debug($"Modbus 请求成功；{requestContext}；尝试次数={attempt}");
                    return result;
                }
                catch (Exception ex) when (attempt == 1 && ex is TimeoutException or ModbusCrcException)
                {
                    // 临时超时和线路干扰可能在下一次恢复，因此只对这两类错误重试一次。
                    lastError = ex;
                    _trace.Warning($"Modbus 请求第 1 次失败，将重试；{requestContext}；原因={ex.Message}");
                }
                catch (OperationCanceledException)
                {
                    // 用户取消、关闭串口等主动取消操作直接向上传递，不能作为超时进行重试。
                    _trace.Information($"Modbus 请求已取消；{requestContext}");
                    throw;
                }
                catch (Exception ex)
                {
                    // 设备异常码、协议结构错误、串口断开等确定性错误不重试。
                    _trace.Error($"Modbus 请求失败；{requestContext}；尝试次数={attempt}；原因={ex.Message}", ex);
                    throw;
                }
            }

            // 只有“第一次可重试错误 + 第二次仍为可重试错误”才会走到这里。
            var finalError = lastError ?? new ModbusProtocolException("Modbus 请求失败。");
            _trace.Error($"Modbus 请求失败；{requestContext}；重试后仍失败；原因={finalError.Message}", finalError);
            throw finalError;
        }
        finally
        {
            // 无论成功、异常还是取消都必须释放锁，否则后续所有通信都会永久阻塞。
            _requestLock.Release();
        }
    }

    /// <summary>
    /// 从完整请求帧提取功能码、寄存器地址范围、数量或写入值，生成人类可读的日志上下文。
    /// </summary>
    /// <param name="request">通常为已经包含 CRC 的 03H 或 06H 请求帧。</param>
    /// <returns>同时包含十进制和十六进制寄存器地址的描述文本。</returns>
    private static string DescribeRequest(ReadOnlySpan<byte> request)
    {
        // 地址、功能码和两个 16 位参数至少需要 6 字节；CRC 是否存在不影响摘要提取。
        if (request.Length < 6)
            return "寄存器地址=未知";

        var function = request[1];
        var startAddress = BinaryPrimitives.ReadUInt16BigEndian(request[2..4]);
        var addressText = $"{startAddress}(0x{startAddress:X4})";
        if (function == 0x03)
        {
            // 03H 的第二个 16 位参数表示寄存器数量，因此可计算闭区间结束地址。
            var count = BinaryPrimitives.ReadUInt16BigEndian(request[4..6]);
            var endAddress = checked((ushort)(startAddress + count - 1));
            return count == 1
                ? $"功能=03H；寄存器地址={addressText}；数量=1"
                : $"功能=03H；寄存器地址={startAddress}～{endAddress}"
                  + $"(0x{startAddress:X4}～0x{endAddress:X4})；数量={count}";
        }

        if (function == 0x06)
        {
            // 06H 的第二个 16 位参数表示写入值，不是数量。
            var value = BinaryPrimitives.ReadUInt16BigEndian(request[4..6]);
            return $"功能=06H；寄存器地址={addressText}；写入值={value}(0x{value:X4})";
        }

        return $"功能={function:X2}H；寄存器地址={addressText}";
    }

    /// <summary>
    /// 按 Modbus RTU 响应结构读取一帧完整数据，并完成 CRC 校验和异常响应识别。
    /// </summary>
    /// <param name="timeout">从开始读取到完整帧接收完毕允许的总时间。</param>
    /// <param name="cancellationToken">调用方主动取消令牌。</param>
    /// <returns>CRC 正确的完整正常响应帧。</returns>
    /// <exception cref="TimeoutException">在指定时间内没有收到完整响应。</exception>
    /// <exception cref="ModbusDeviceException">从机返回功能码最高位为 1 的异常响应。</exception>
    /// <exception cref="ModbusCrcException">完整帧的 CRC 校验失败。</exception>
    private async Task<byte[]> ReadResponseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // 关联令牌同时响应调用方取消和内部响应超时。
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            // 所有响应的前两个字节都是从机地址和功能码。
            // 第三个字节在正常 03H 响应中是数据字节数，在异常响应中则是异常码。
            var header = new byte[3];
            await ReadExactlyAsync(header.AsMemory(0, 2), timeoutSource.Token);

            // Modbus 规定异常响应功能码 = 原功能码 | 0x80。
            var isException = (header[1] & 0x80) != 0;
            if (isException)
            {
                // 异常帧固定为 5 字节：
                // [从机地址][异常功能码][异常码][CRC低][CRC高]。
                var tail = new byte[3];
                await ReadExactlyAsync(tail, timeoutSource.Token);
                var exceptionFrame = new[] { header[0], header[1], tail[0], tail[1], tail[2] };
                ValidateCrc(exceptionFrame);
                throw new ModbusDeviceException(tail[0]);
            }

            // 正常 03H 响应第三字节给出后续寄存器数据的字节数。
            await ReadExactlyAsync(header.AsMemory(2, 1), timeoutSource.Token);

            // 数据部分后面还包含两个 CRC 字节。
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
            // 只有内部定时器触发时才转换为 TimeoutException；
            // 调用方主动取消产生的 OperationCanceledException 保持原样向上传递。
            throw new TimeoutException($"设备在 {timeout.TotalMilliseconds:0} ms 内未返回完整响应。");
        }
    }

    /// <summary>
    /// 重复调用传输层，直到指定缓冲区被完全填满。
    /// </summary>
    /// <param name="buffer">需要填满的目标缓冲区。</param>
    /// <param name="cancellationToken">用于取消分段读取的令牌。</param>
    /// <remarks>
    /// 串口一次 <c>ReadAsync</c> 不保证返回请求的全部字节。例如 USB 转串口可能把一个 RTU 帧拆成多段，
    /// 因此必须循环累计，而不能把一次短读误认为完整响应。
    /// </remarks>
    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            // 只把尚未填充的切片交给传输层，已经接收的字节不会被覆盖。
            var count = await _transport.ReadAsync(buffer[offset..], cancellationToken);
            if (count <= 0)
            {
                // 返回 0 表示流已经结束或端口被关闭，继续循环只会造成死循环。
                throw new EndOfStreamException("串口在响应完成前已关闭。");
            }

            // 下次从新偏移继续读取，直到累计字节数等于目标长度。
            offset += count;
        }
    }

    /// <summary>
    /// 验证并解析正常 03H 响应中的寄存器数据。
    /// </summary>
    /// <param name="frame">已经通过 CRC 校验的完整响应帧。</param>
    /// <param name="slaveAddress">请求中指定的从机地址，用于防止接收其他设备的响应。</param>
    /// <param name="count">请求的寄存器数量，用于校验响应字节数。</param>
    /// <returns>按寄存器地址顺序排列的 16 位原始值数组。</returns>
    /// <exception cref="ModbusProtocolException">
    /// 从机地址、功能码、数据字节数或帧总长度与请求不匹配。
    /// </exception>
    private static ushort[] ParseReadResponse(byte[] frame, byte slaveAddress, ushort count)
    {
        // RS485 总线上可能连接多个从机，必须确认响应来自本次请求的目标地址。
        if (frame[0] != slaveAddress)
        {
            throw new ModbusProtocolException($"响应从机地址不匹配：期望 {slaveAddress}，收到 {frame[0]}。");
        }

        // 本解析器只接受正常 03H 响应。异常功能码已经在 ReadResponseAsync 中处理。
        if (frame[1] != 0x03)
        {
            throw new ModbusProtocolException($"响应功能码不匹配：期望 03H，收到 {frame[1]:X2}H。");
        }

        // 每个保持寄存器固定占两个字节；正常帧另外包含地址、功能码、字节数和两个 CRC 字节，
        // 因此完整长度应为 count * 2 + 5。
        var expectedBytes = count * 2;
        if (frame[2] != expectedBytes || frame.Length != expectedBytes + 5)
        {
            throw new ModbusProtocolException("03H 响应字节数与请求寄存器数量不一致。");
        }

        var values = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            // Modbus 寄存器内部按高字节、低字节传输，使用大端序解码为 ushort。
            values[index] = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3 + index * 2, 2));
        }

        return values;
    }

    /// <summary>
    /// 校验完整 RTU 响应帧的 CRC，不匹配时抛出包含原始帧的异常。
    /// </summary>
    /// <param name="frame">包含末尾 CRC 低、高字节的完整响应帧。</param>
    /// <exception cref="ModbusCrcException">CRC 与帧内容不匹配。</exception>
    private static void ValidateCrc(ReadOnlySpan<byte> frame)
    {
        if (!Crc16Modbus.IsValid(frame))
        {
            throw new ModbusCrcException($"响应 CRC 校验失败：{Convert.ToHexString(frame)}");
        }
    }

    /// <summary>
    /// 根据波特率等待至少 3.5 个字符时间，形成 Modbus RTU 帧间静默区间。
    /// </summary>
    /// <param name="baudRate">当前串口波特率。</param>
    /// <param name="cancellationToken">用于取消等待的令牌。</param>
    /// <returns>表示静默间隔等待的任务。</returns>
    private static Task EnforceSilentIntervalAsync(int baudRate, CancellationToken cancellationToken)
    {
        // 8N1 每字符包含 1 个起始位、8 个数据位和 1 个停止位，共约 10 bit。
        // 一个字符所需毫秒数约为 10 / baudRate * 1000，3.5 个字符即 35_000 / baudRate ms。
        // 公式写成 3.5 * 10_000 / baudRate，结果向上取整，避免实际等待短于协议要求。
        // 同时设置 2 ms 下限，以兼容高速 USB 转 RS485 转换器及系统定时器精度。
        var milliseconds = Math.Max(2, (int)Math.Ceiling(3.5 * 10_000d / baudRate));
        return Task.Delay(milliseconds, cancellationToken);
    }

    /// <summary>
    /// 释放底层传输和请求互斥锁。
    /// </summary>
    /// <remarks>
    /// 调用方应确保不再发起新请求后再释放客户端。底层传输负责关闭或释放其持有的串口资源。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // 先释放实际 I/O 资源，再释放仅用于本客户端内部同步的信号量。
        await _transport.DisposeAsync();
        _requestLock.Dispose();
    }
}
