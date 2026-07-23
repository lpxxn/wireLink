namespace WireLink.Core.Communication;

/// <summary>
/// 串口连接参数。协议固定使用 8 个数据位、无校验、1 个停止位和无流控，
/// 因此这里只暴露现场需要调整的端口、波特率和超时时间。
/// </summary>
public sealed record SerialConnectionOptions(
    string PortName,
    int BaudRate,
    TimeSpan ReadTimeout,
    TimeSpan WriteTimeout);

/// <summary>
/// 面向字节流的传输抽象。Modbus 层不直接依赖 <c>SerialPort</c>，
/// 单元测试可以使用内存传输，正式程序则使用真实或虚拟串口。
/// </summary>
public interface IByteTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    ValueTask DiscardInputAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}

/// <summary>
/// 协议追踪接口。原始收发帧和每个字段的计算过程均通过该接口记录，
/// 便于现场排查字序、倍率和 CRC 问题。
/// </summary>
public interface IProtocolTrace
{
    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class NullProtocolTrace : IProtocolTrace
{
    public static NullProtocolTrace Instance { get; } = new();

    private NullProtocolTrace() { }

    public void Debug(string message) { }
    public void Information(string message) { }
    public void Warning(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

/// <summary>应用使用的最小 Modbus RTU 主站接口。</summary>
public interface IModbusRtuClient : IAsyncDisposable
{
    bool IsOpen { get; }

    ValueTask OpenAsync(SerialConnectionOptions options, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    Task<ushort[]> ReadHoldingRegistersAsync(
        byte slaveAddress,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default);

    Task WriteSingleRegisterAsync(
        byte slaveAddress,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default);
}

/// <summary>串口枚举接口，界面展开下拉框时即时调用。</summary>
public interface ISerialPortCatalog
{
    IReadOnlyList<string> GetPortNames();
}
