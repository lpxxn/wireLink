# WireLink

WireLink 是面向 USB 转 RS485 设备的跨平台 Modbus RTU 读取、解析、展示和 Excel 导出工具。首版支持设备数据与历史故障记录；录波页保留协议待补充空状态。

## 快速开始

环境要求：.NET SDK 10。首次构建会从 NuGet 获取 Avalonia、Semi、Ursa、ReactiveUI、ClosedXML 和 Serilog。

```bash
dotnet restore WireLink.slnx
dotnet build WireLink.slnx
dotnet test tests/WireLink.Tests/WireLink.Tests.csproj
dotnet run --project src/WireLink.App/WireLink.App.csproj
```

操作顺序：选择或输入串口 → 选择波特率 → 打开串口 → 输入设备地址并选择 BW1/BW3 控制器 → 连接测试 → 读取设备/故障数据。程序恢复上次设置，但不会自动打开串口。

macOS 26 若调试运行提示 `libSkiaSharp.dylib ... library load disallowed by system policy`，请按 [发布与签名](docs/release.md) 的调试签名段处理。

## 功能状态

- 已实现：端口动态枚举和手动输入、8N1 串口、03H、06H、CRC、超时/CRC 重试一次、异常响应、请求串行化、分区读取和部分失败保留。
- 已实现：设备与故障四列双组表、自动刷新、连续失败停止、浅/深/系统主题、固定 uint32 高字优先解析、Excel 导出。
- 已实现：F12 非模态日志窗、Debug 原始帧与逐字段公式、滚动文件日志、JSON 设置。
- 已实现：独立虚拟串口模拟器及超时、CRC、异常码注入。
- 暂不实现：真实录波读取和曲线、设备参数编辑、遥控、安装器与生产代码签名。

## 文档索引

- [开发计划](docs/development-plan.md)
- [架构与维护](docs/architecture.md)
- [协议解析与未确认规则](docs/protocol.md)
- [模拟器与虚拟串口](docs/simulator.md)
- [测试说明](docs/testing.md)
- [发布、签名与串口权限](docs/release.md)

根目录 [README.md](../README.md) 是原始需求和“待补充协议规则”的权威清单；协议确认后应同时修改该清单、寄存器目录、解析器和测试。
