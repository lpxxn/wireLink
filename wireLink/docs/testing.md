# 测试说明

## 自动化测试

```bash
dotnet test tests/WireLink.Tests/WireLink.Tests.csproj
```

当前覆盖 CRC 标准帧、分段响应、高低字节、CRC 一次重试、设备异常不重试、uint32 两种字序、`[514,513]`、BW1/BW3 额定电流序值映射、电流变比 1/2 及非法序值、百分比原值展示、事件数据 0、完整 BCD 时间和非法 BCD、续寄存器去重、寄存器地址与解析异常日志、Excel 四列约束、模拟器 03H/06H 与错误 CRC。录波测试额外覆盖 PDF 全部 18 个完整响应帧的帧长、byte-count、CRC 和拼接 SHA-256，逐块校验模拟器的 1152 个寄存器值，并校验三相各 384 点的拼接顺序、固定 RMS 和时间轴；B200H/B580H 仍保留可读的固定数组与端序测试。此外还覆盖首块失败中止、非法地址间隙和录波 Excel 两张工作表。

录波原始点明细测试固定校验 384 行、1152 个三相值、首末时间、段内序号、三相源地址、有符号 AD，以及负数转换为 16 位补码十六进制和无符号十进制原值的结果。原始值曲线测试锁定 `-1 → 65535`、`-320 → 65216`、`-32768 → 32768`，并校验三相显隐不会丢失底层点数据。

### 调试 PDF 全部三相数据

`WaveformPdfCompleteThreePhaseTests` 提供两个专用测试：

- `Pdf_all_18_blocks_follow_documented_time_then_phase_order` 使用独立的显式地址表校验 18 次读取顺序、时间段、相别、帧长、CRC 和整组 SHA-256。
- `Pdf_complete_three_phase_snapshot_exposes_every_sample_for_debugging` 生成 `snapshot` 调试对象。可在测试中标注的断点行展开 `Blocks`、`PhaseA`、`PhaseB`、`PhaseC` 和 `Points`；详细测试输出包含 18 块各 64 点及 384 行三相对齐数据。

命令行查看完整输出：

```powershell
dotnet test tests/WireLink.Tests/WireLink.Tests.csproj --filter "FullyQualifiedName~WaveformPdfCompleteThreePhaseTests" --logger "console;verbosity=detailed"
```

新增协议规则时至少增加一个正常用例和一个边界/非法用例。外部 PTY/COM 集成测试依赖机器环境，不应使普通 CI 失败；找不到虚拟串口时应明确跳过。

## 人工检查

1. 浅色、深色、跟随系统下检查主窗口和日志窗。
2. 调整窗口到最小尺寸，确认左右区域与表格滚动条可用。
3. 验证关闭串口前后控件启用状态、自动刷新、三次失败停止。
4. 用模拟器逐项触发超时、CRC、02/03/04 和断开。
5. 导出设备/故障 Excel，确认只有四列展示值，没有地址、原值、隐藏字段或诊断页，并且单元格不带颜色、粗体、边框等样式。
6. 用模拟器读取录波，确认三相曲线均为 384 点、可独立显隐，X 轴为 -80～39.6875 ms，Y 轴和 RMS 均标为 AD。
7. 导出录波 Excel，确认“波形数据”有 384 行三相对齐数据，“读取明细”有 1152 行并保留十进制/十六进制源地址，数据单元格为数值类型。

## 实机清单

记录设备型号、固件、USB-RS485 芯片和 OS；验证端口枚举、256 连接测试、每个区间、uint32 字序、倍率、785 三种类型与 0～15 编码、0 为最近一条、06H 写 785 后约 100 ms 再读、最大响应时间和拔线恢复。录波字节序已由厂商确认为大端；实机继续确认 3200 Hz 推导、六段连续性、AD→A 标定、有效标志及 18 次读取期间的冻结/快照行为。保留 Debug 日志作为协议确认附件。
