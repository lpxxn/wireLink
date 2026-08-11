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
- 录波：`WaveformCatalog` 固化 18 个读取块，`WaveformDataService` 负责有符号解码、时间轴对齐和 RMS；采样流与普通寄存器轮询共用 `IModbusRtuClient` 的请求锁。

## 状态与并发

串口打开和设备连接是两个独立状态。打开串口不代表设备应答；连接测试固定读取 256。所有 Modbus 请求在 `ModbusRtuClient` 内通过 `SemaphoreSlim` 串行执行。关闭串口先取消自动刷新和当前操作，掉线不会自动重连。

设备页按最小业务块读取：单个 uint16 字段独立读取，uint32 的两个寄存器作为一个不可拆分业务块读取。1552 会优先独立读取，解析器只取其 bit0～bit7 作为额定电流序值。成功字段更新；失败业务块的旧字段标记 `Stale`，不会影响其他无业务关系字段。连续三轮含失败会停止自动刷新并清除设备连接状态。故障页一次读取只写一次 785；记录区 768～786、额定电流配置 1552 与总操作次数 1031 分开读取，任一区间失败时仍展示其他区间的成功数据。

录波页严格按时间段和 A/B/C 相顺序发起 18 次读取。任一块最终失败即终止本次读取，不构造残缺 `WaveformData`；ViewModel 只在完整成功后原子替换曲线，因此上一次完整结果不会与新批次混合。页面和 Excel 统一使用有符号原始 AD 值，未获得标定前不标成安培。

## 配置和日志位置

- 配置：系统 ApplicationData 下 `WireLink/settings.json`。
- 日志：系统 LocalApplicationData 下 `WireLink/logs/wirelink-*.log`。
- 配置只保存用户选项，不保存串口或设备已连接状态。
- 所有寄存器相关日志必须包含十进制和十六进制地址。TX/RX、重试和通讯失败记录请求地址范围；字段解析记录字段名、地址、原始值、公式和结果；缺失地址记录期望地址与实际缺失地址；解析失败使用 Error 级别并附带异常堆栈。

## 注释和变更纪律

公共协议接口、地址顺序、异常恢复和未确认规则必须写中文 XML 注释。协议未确认值必须使用 `ProtocolUnconfirmed`/`InvalidData` 并提供 Warning；不得在 UI 层静默猜测。
