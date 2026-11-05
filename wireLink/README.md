# WireLink

WireLink 是面向 USB 转 RS485 设备的跨平台 Modbus RTU 读取、解析、展示和 Excel 导出工具。当前支持框架控制器和塑壳断路器；框架控制器提供设备、保护、历史故障和录波数据，塑壳断路器提供设备与保护数据。

## 快速开始

环境要求：.NET SDK 10。首次构建会从 NuGet 获取 Avalonia、Semi、Ursa、ReactiveUI、ClosedXML 和 Serilog。

```bash
dotnet restore WireLink.slnx
dotnet build WireLink.slnx
dotnet test tests/WireLink.Tests/WireLink.Tests.csproj
dotnet run --project src/WireLink.App/WireLink.App.csproj
```

```
dotnet build .\src\WireLink.App -t:Rebuild
dotnet build .\src\WireLink.App\WireLink.App.csproj --target Rebuild

```

实际上，如果你的目的只是干净地重新编译整个项目，我更建议：
```
dotnet clean .\src\WireLink.App\WireLink.App.csproj
dotnet build .\src\WireLink.App\WireLink.App.csproj
```

操作顺序：选择设备类型、串口和波特率 → 打开串口 → 输入设备地址 → 连接测试 → 读取该设备支持的数据。切换设备类型会保留已打开的串口，但会停止刷新、清空旧数据并要求重新连接测试。程序恢复上次设置，但不会自动打开串口。

macOS 26 若调试运行提示 `libSkiaSharp.dylib ... library load disallowed by system policy`，请按 [发布与签名](docs/release.md) 的调试签名段处理。

## 功能状态

- 已实现：端口动态枚举和手动输入、8N1 串口、03H、06H、CRC、超时/CRC 重试一次、异常响应、请求串行化、分区读取和部分失败保留。
- 已实现：设备与故障四列双组表、自动刷新、连续失败停止、浅/深/系统主题、固定 uint32 高字优先解析、Excel 导出。
- 已实现：框架控制器保护数据（1280、1282～1288、1296～1301，依赖 1552/1793）、塑壳断路器设备/保护数据、设备类型切换、设备专属连接探测地址，以及 2400/4800 BPS。
- 已实现：F12 非模态日志窗、Debug 原始帧与逐字段公式、滚动文件日志、JSON 设置。
- 已实现：独立虚拟串口模拟器及超时、CRC、异常码注入。
- 已实现：录波页固定选择故障第 0 条记录（最近一条）并读取 768～770 的有效 BCD 时间，不跟随故障数据页的类型和序号选择；随后读取 1552 框架等级和 18 块固定录波。秒级故障时间会绑定一个持久化的软件补充毫秒，主图和主 Excel 使用绝对时间轴并明确其来源。每个64点单相块读取成功即计算RMS(A)，同段三相齐全即预览有符号AD曲线与分块RMS；悬停任意绘图区时间段可查看三相RMS，失败或取消恢复上次完整结果。主Excel每64行AD后追加一行三相RMS(A)，另保留1152行原始读取明细；Shift+F8 仍提供相对时间、有符号 AD、uint16 原值、源地址和原始值曲线；模拟器默认使用用户Excel明细样本，--waveform-source pdf可切回PDF的18帧协议样例；两者均有原始值及RMS回归测试。
- 已实现：F9独立RMS计算器，输入Rate和一块有符号AD数据，显示公式、平方和、AD-RMS与安培RMS及代入过程；支持填入64点示例，无需连接设备。
- 暂不实现：多条录波选择、设备参数编辑、遥控、安装器与生产代码签名。

## 录波RMS口径更新（2026-11-04）

按用户确认，硬件已完成N=64相关处理，软件使用 `sqrt(sum(AD²))`，不再除N；64点仍用于块完整性校验。安培换算系数和Rate映射不变，最终才保留1位小数。用户64点示例在框II/Rate=1.5时为3220.9A，旧402.6A已停用。此约定仅适用于当前硬件输出。F9/Excel旧公式文字及测试基准仍待同步，见[RMS说明](docs/waveform-rms-explained.md)。

## 文档索引

- [开发计划](docs/development-plan.md)
- [架构与维护](docs/architecture.md)
- [协议解析与未确认规则](docs/protocol.md)
- [框架控制器保护数据](docs/frame-controller-protection.md)
- [decimal scale：如何取出倍率的小数位数](docs/decimal-scale-explained.md)
- [塑壳断路器协议实现](docs/molded-case-circuit-breaker.md)
- [故障录波协议解析](docs/waveform-protocol.md)
- [录波 RMS 公式与解析代码说明](docs/waveform-rms-explained.md)
- [录波功能实现计划](docs/waveform-implementation-plan.md)
- [模拟器与虚拟串口](docs/simulator.md)
- [测试说明](docs/testing.md)
- [发布、签名与串口权限](docs/release.md)

根目录 [README.md](../README.md) 是原始需求和“待补充协议规则”的权威清单；协议确认后应同时修改该清单、寄存器目录、解析器和测试。


## 第一次运行慢
第一次运行时 Windows 往往会做这些事情：
Microsoft Defender/其他杀毒软件扫描整个 EXE。
Windows 检查文件签名、来源和安全信誉。
.NET 首次加载运行库、Avalonia、字体以及图形组件。
程序文件第一次从磁盘读入内存。
SkiaSharp 可能初始化图形和字体缓存。
第二次启动时，文件和运行库已经进入 Windows 文件缓存，安全扫描也可能复用结果，所以会快很多。
