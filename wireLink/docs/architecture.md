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
- 录波：单独新增服务和模型。采样流与普通寄存器轮询共用 `IModbusRtuClient` 的请求锁。

## 状态与并发

串口打开和设备连接是两个独立状态。打开串口不代表设备应答；连接测试固定读取 256。所有 Modbus 请求在 `ModbusRtuClient` 内通过 `SemaphoreSlim` 串行执行。关闭串口先取消自动刷新和当前操作，掉线不会自动重连。

设备页逐区间读取。成功字段更新；失败区间的旧字段标记 `Stale`。连续三轮含失败会停止自动刷新并清除设备连接状态。故障页一次读取只写一次 785。

## 配置和日志位置

- 配置：系统 ApplicationData 下 `WireLink/settings.json`。
- 日志：系统 LocalApplicationData 下 `WireLink/logs/wirelink-*.log`。
- 配置只保存用户选项，不保存串口或设备已连接状态。

## 注释和变更纪律

公共协议接口、地址顺序、异常恢复和未确认规则必须写中文 XML 注释。协议未确认值必须使用 `ProtocolUnconfirmed`/`InvalidData` 并提供 Warning；不得在 UI 层静默猜测。
