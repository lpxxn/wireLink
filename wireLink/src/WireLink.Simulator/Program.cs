using System.IO.Ports;
using WireLink.Simulator;

var arguments = args.Select((value, index) => (value, index)).ToDictionary(x => x.value, x => x.index);
string Option(string name, string fallback) => arguments.TryGetValue(name, out var i) && i + 1 < args.Length ? args[i + 1] : fallback;
var portName = Option("--port", "");
var baud = int.Parse(Option("--baud", "9600"));
var address = byte.Parse(Option("--address", "1"));
if (string.IsNullOrWhiteSpace(portName))
{
    Console.Error.WriteLine("用法：WireLink.Simulator --port <串口> [--baud 9600] [--address 1]");
    return 2;
}

using var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One) { Handshake = Handshake.None };
var engine = new SimulatorEngine(address);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
port.Open();
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
                offset += await port.BaseStream.ReadAsync(frame.AsMemory(offset), shutdown.Token);
            engine.Tick();
            var response = engine.Process(frame);
            if (response is not null) await port.BaseStream.WriteAsync(response, shutdown.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { Console.Error.WriteLine($"串口错误：{ex.Message}"); await Task.Delay(200, shutdown.Token); }
    }
});

while (!shutdown.IsCancellationRequested)
{
    var line = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (line is null or "quit" or "exit") { shutdown.Cancel(); break; }
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    switch (parts.FirstOrDefault())
    {
        case "normal": engine.FaultMode=SimulatorFaultMode.Normal; break;
        case "timeout": engine.FaultMode=parts.ElementAtOrDefault(1)=="continuous" ? SimulatorFaultMode.TimeoutContinuous : SimulatorFaultMode.TimeoutOnce; break;
        case "crc": engine.FaultMode=SimulatorFaultMode.BadCrcOnce; break;
        case "exception": engine.ExceptionCode=byte.Parse(parts.ElementAtOrDefault(1) ?? "02"); engine.FaultMode=SimulatorFaultMode.ExceptionOnce; break;
        case "disconnect": shutdown.Cancel(); port.Close(); break;
        case "fault": engine.Registers.GetType(); Console.WriteLine("故障记录可通过主程序的类型/序号选择读取。"); break;
        case "status": Console.WriteLine($"模式={engine.FaultMode}，从机={address}，寄存器={engine.Registers.Count}"); break;
        case "help": Console.WriteLine("normal | timeout [continuous] | crc | exception 02|03|04 | disconnect | fault <type> | status | quit"); break;
        default: Console.WriteLine("未知命令，输入 help 查看帮助。"); break;
    }
}
try { await serialTask; } catch (OperationCanceledException) { }
return 0;
