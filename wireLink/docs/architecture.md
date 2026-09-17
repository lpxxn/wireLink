# 架构与维护指南

## 依赖方向

```text
WireLink.App ──────────────┐
                          ├─> WireLink.Core
WireLink.Infrastructure ──┘
WireLink.Simulator ──────────> WireLink.Core
WireLink.Tests ──────────────> 全部项目
```

Core 不依赖 Avalonia、串口、Excel 或日志实现。App 只编排服务并管理界面状态；Infrastructure 处理 OS 和文件系统；Simulator 是可单独发布的控制台从站。

## 关键扩展点

- 新通信介质：实现 `IByteTransport`，无需修改 Modbus 与解析层。
- 新寄存器：在 `RegisterCatalog` 添加定义和读取区间；多寄存器字段只增加 `Addresses`，不要添加空名称显示项。
- 新解析规则：扩展 `ValueTransform`，在 `RegisterParser.ParseOne` 增加纯函数分支和测试。
- 新导出格式：实现 `IExcelExportService` 或增加新的导出接口；不要让 ViewModel 直接依赖 ClosedXML。
- 录波：`FaultRecordService.ReadTimestampAsync` 固定选择故障第 0 条记录（最近一条）并读取 768～770；`JsonWaveformTimeOffsetStore` 按秒级故障时间保存稳定的软件补充毫秒；`WaveformCatalog` 固化 18 个读取块；`WaveformDataService` 读取 1552 框架等级后负责有符号解码、AD→A 标定、相对时间对齐和 RMS；`WaveformTiming` 再把相对时间映射到绝对时间。所有通信共用 `IModbusRtuClient` 的请求锁。

## 状态与并发

串口打开和设备连接是两个独立状态。打开串口不代表设备应答；连接测试固定读取 256。所有 Modbus 请求在 `ModbusRtuClient` 内通过 `SemaphoreSlim` 串行执行。关闭串口先取消自动刷新和当前操作，掉线不会自动重连。

设备页按最小业务块读取：单个 uint16 字段独立读取，uint32 的两个寄存器作为一个不可拆分业务块读取。1552 会优先独立读取，解析器只取其 bit0～bit7 作为额定电流序值。成功字段更新；失败业务块的旧字段标记 `Stale`，不会影响其他无业务关系字段。连续三轮含失败会停止自动刷新并清除设备连接状态。故障页一次读取只写一次 785；记录区 768～785、额定电流配置 1552 与总操作次数 1031 分开读取，任一区间失败时仍展示其他区间的成功数据。

录波页先用 06H 向 785 写入 `0000H`，固定选择故障第 0 条记录（最近一条），等待配置的准备时间，再用 03H 读取 768～770 并完成 BCD、日期范围和全零校验。此流程不使用故障数据页当前选择的报警/变位类型或记录序号。只有故障时间有效且稳定毫秒已成功持久化，才继续读取 1552 和 18 个录波块。故障时间无效时不得请求 1552 或录波区；1552 失败、框架等级不是 0/1/2、毫秒文件无法保存或任一录波块最终失败时，也不构造新的页面结果。ViewModel 只在整批完整成功后原子替换曲线，因此上一次完整结果不会与新批次混合。主图和主 Excel 使用安培与绝对时间，模型仍保留相对毫秒；Shift+F8 明细、原始图和明细专用 Excel 保持相对毫秒及原始 uint16、有符号 AD 和源地址。

## 配置和日志位置

- 配置：系统 ApplicationData 下 `WireLink/settings.json`。
- 日志：系统 LocalApplicationData 下 `WireLink/logs/wirelink-*.log`。
- 配置只保存用户选项，不保存串口或设备已连接状态。
- 所有寄存器相关日志必须包含十进制和十六进制地址。TX/RX、重试和通讯失败记录请求地址范围；字段解析记录字段名、地址、原始值、公式和结果；缺失地址记录期望地址与实际缺失地址；解析失败使用 Error 级别并附带异常堆栈。

## 注释和变更纪律

公共协议接口、地址顺序、异常恢复和未确认规则必须写中文 XML 注释。协议未确认值必须使用 `ProtocolUnconfirmed`/`InvalidData` 并提供 Warning；不得在 UI 层静默猜测。
