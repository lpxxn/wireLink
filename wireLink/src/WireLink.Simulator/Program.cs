using System.IO.Ports;
using WireLink.Simulator;

var arguments = args.Select((value, index) => (value, index)).ToDictionary(x => x.value, x => x.index);
string Option(string name, string fallback) => arguments.TryGetValue(name, out var i) && i + 1 < args.Length ? args[i + 1] : fallback;
var portName = Option("--port", "");
var baud = int.Parse(Option("--baud", "9600"));
var address = byte.Parse(Option("--address", "1"));
var protectionModeText = Option("--protection-mode", "3");
if (string.IsNullOrWhiteSpace(portName))
{
    PrintUsage();
    return 2;
}
if (!byte.TryParse(protectionModeText, out var protectionMode) || protectionMode > 3)
{
    Console.Error.WriteLine("保护模式必须是 0～3：0=关闭，1=漏电型，2=差值型，3=地电流型。");
    PrintUsage();
    return 2;
}

using var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One) { Handshake = Handshake.None };
var engine = new SimulatorEngine(address);
engine.SetGroundProtectionMode(protectionMode);
using var shutdown = new CancellationTokenSource();
port.Open();

void RequestShutdown()
{
    if (!shutdown.IsCancellationRequested) shutdown.Cancel();
    try { if (port.IsOpen) port.Close(); } catch { }
}

Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestShutdown(); };
Console.WriteLine($"WireLink 模拟器已打开 {portName}，{baud} BPS，从机 {address}。输入 help 查看命令。");

var serialTask = Task.Run(async () =>
{
    var frame = new byte[8];
    while (!shutdown.IsCancellationRequested)
    {
        try
        {
            var offset = 0;
            while (offset < frame.Length)
            {
                var count = await port.BaseStream.ReadAsync(frame.AsMemory(offset), shutdown.Token);
                if (count <= 0) throw new EndOfStreamException("串口已关闭。");
                offset += count;
            }
            engine.Tick();
            var response = engine.Process(frame);
            if (response is not null) await port.BaseStream.WriteAsync(response, shutdown.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception) when (shutdown.IsCancellationRequested) { break; }
        catch (Exception ex) when (!shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine($"串口错误：{ex.Message}");
            try { await Task.Delay(200, shutdown.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
});

_ = Task.Run(() =>
{
    while (!shutdown.IsCancellationRequested)
    {
        var line = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (line is null or "quit" or "exit") { RequestShutdown(); break; }
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault())
        {
            case "normal": engine.FaultMode=SimulatorFaultMode.Normal; break;
            case "timeout": engine.FaultMode=parts.ElementAtOrDefault(1)=="continuous" ? SimulatorFaultMode.TimeoutContinuous : SimulatorFaultMode.TimeoutOnce; break;
            case "crc": engine.FaultMode=SimulatorFaultMode.BadCrcOnce; break;
            case "exception": engine.ExceptionCode=byte.Parse(parts.ElementAtOrDefault(1) ?? "02"); engine.FaultMode=SimulatorFaultMode.ExceptionOnce; break;
            case "disconnect": RequestShutdown(); break;
            case "current": SetCurrent(parts.ElementAtOrDefault(1)); break;
            case "fault": SetCurrent("fault"); break;
            case "alarm": SetCurrent("alarm"); break;
            case "protection" or "ground": SetProtectionMode(parts.ElementAtOrDefault(1)); break;
            case "status": Console.WriteLine($"模式={engine.FaultMode}，当前事件={engine.CurrentEventMode}，保护模式={DescribeProtectionMode(engine.GroundProtectionMode)}，从机={address}，寄存器={engine.RegisterCount}"); break;
            case "help": PrintCommands(); break;
            default: Console.WriteLine("未知命令，输入 help 查看帮助。"); break;
        }
    }
});

try { await serialTask; } catch (OperationCanceledException) { }
catch (Exception) when (shutdown.IsCancellationRequested) { }
return 0;

void SetCurrent(string? value)
{
    var mode = value switch
    {
        "normal" or "none" or "clear" => SimulatorCurrentEventMode.Normal,
        "fault" => SimulatorCurrentEventMode.Fault,
        "alarm" => SimulatorCurrentEventMode.Alarm,
        _ => (SimulatorCurrentEventMode?)null,
    };
    if (mode is null)
    {
        Console.WriteLine("用法：current normal|fault|alarm");
        return;
    }

    engine.SetCurrentEvent(mode.Value);
    Console.WriteLine($"当前事件已切换为 {mode.Value}");
}

void SetProtectionMode(string? value)
{
    if (!byte.TryParse(value, out var mode) || mode > 3)
    {
        Console.WriteLine("用法：protection 0|1|2|3（0=关闭，1=漏电型，2=差值型，3=地电流型）");
        return;
    }

    engine.SetGroundProtectionMode(mode);
    Console.WriteLine($"1793.bit12～bit10 已切换为 {DescribeProtectionMode(mode)}");
}

static string DescribeProtectionMode(byte mode) => mode switch
{
    0 => "0（关闭）",
    1 => "1（漏电型）",
    2 => "2（差值型，保留原值并提示未确认）",
    3 => "3（地电流型）",
    _ => $"{mode}（未知）",
};

static void PrintCommands() => Console.WriteLine(
    "normal | timeout [continuous] | crc | exception 02|03|04 | current normal|fault|alarm | " +
    "fault | alarm | protection 0|1|2|3 | disconnect | status | quit");

static void PrintUsage() => Console.Error.WriteLine(
    "用法：WireLink.Simulator --port <串口> [--baud 9600] [--address 1] [--protection-mode 0|1|2|3]");
