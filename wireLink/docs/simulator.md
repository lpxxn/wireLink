# 虚拟串口模拟器

模拟器必须连接虚拟串口对的一端，主程序连接另一端；两者均完整经过 `System.IO.Ports`。

## macOS

安装并创建 PTY 对：

```bash
brew install socat
socat -d -d pty,raw,echo=0,link=/tmp/wirelink-app pty,raw,echo=0,link=/tmp/wirelink-device
```

另开终端启动模拟器：

```bash
dotnet run --project src/WireLink.Simulator -- --port /tmp/wirelink-device --baud 9600 --address 1 --protection-mode 3
```

主程序端口手动输入 `/tmp/wirelink-app`。

## Windows

使用 com0com 等工具创建成对端口（例如 COM10/COM11）。以管理员身份打开 com0com 的 **Setup Command Prompt**，常用命令如下：

```text
# 查看帮助
help

# 查看已有端口对及其编号
list

# 创建 COM10/COM11 端口对
install PortName=COM10 PortName=COM11

# 创建使用默认名称（CNCA<n>/CNCB<n>）的端口对
install - -

# 修改已有端口名称
change CNCA0 PortName=COM10
change CNCB0 PortName=COM11

# 删除编号为 0 的端口对；先用 list 确认编号
remove 0

# 退出配置工具
quit
```

也可以在 com0com 安装目录中直接执行非交互式命令，例如 `setupc.exe list`。端口对准备好后，启动模拟器：

```powershell
dotnet run --project src/WireLink.Simulator -- --port COM11 --baud 9600 --address 1 --protection-mode 3
```

主程序选择 COM10。正式部署应使用经过组织安全审查且与 Windows 版本兼容的虚拟 COM 驱动。

## 运行时命令

- `normal`：恢复正常。
- `timeout`：下一请求不响应；`timeout continuous`：持续不响应。
- `crc`：下一响应发送错误 CRC。
- `exception 02|03|04`：下一响应返回异常码。
- `disconnect`：关闭模拟器串口。
- `current normal`：清除当前故障/报警，512 显示普通合闸状态。
- `current fault` 或 `fault`：设置当前过载故障样例，512 置故障标志，515/516～523 返回当前故障数据。
- `current alarm` 或 `alarm`：设置当前过载预报警样例，512 置报警标志，513～514/515/516 返回当前报警数据。
- `protection 0`：把 `1793.bit12～bit10` 改为 0“关闭”，模式相关字段保留原值。
- `protection 1`：改为 1“漏电型”，主程序按漏电规则解析。
- `protection 2`：改为 2“差值型”，主程序保留原值并显示未确认提示。
- `protection 3`：改为 3“地电流型”，主程序按接地规则解析。`ground 0|1|2|3` 是同一命令的别名。
- `status`：显示故障注入模式、当前事件、1793 保护模式、地址和寄存器数。
- `quit`：退出。

启动参数 `--protection-mode` 同样只接受 0～3，省略时默认为 3“地电流型”。运行期间执行 `protection` 命令只改写 1793 的 bit12～bit10，寄存器其他位保持不变；主程序重新点击保护页“立即读取”后即可看到新模式的解析结果。

模拟 uint32 始终按高字优先编码。电压和电流会缓慢变化；当前热容 279 的样例值为 `68%`，总操作次数 1031 的样例值为 `128`。寄存器 1552 返回 `0x0104`：bit0～bit7 的序值是 4，BW1/BW3 均映射为 630A、变比 1；bit8～bit11 的框架等级是 1，录波换算使用 `Rate=1.5`；bit12～bit15 为 0。主程序不读取寄存器 788。


## 录波样本

默认使用用户提供的 `录波原始点明细_20261104_084756.xlsx` 的384行三相原始采样，共1152个寄存器值。启动时会显示样本名称；18块仍按时间段A/B/C顺序映射，负数保留16位补码，不平滑、不缩放原始采样。

```powershell
dotnet run --project src/WireLink.Simulator -- --port COM11 --baud 9600 --address 1 --waveform-source recorded
```

如需复现原协议PDF的样本，指定 `--waveform-source pdf`。省略该参数等同于 `recorded`。录波样本已内置到模拟器，不依赖微信缓存路径或运行时安装Excel；本选项选择固定样本，不是通用Excel文件导入。

源工作表是“录波原始点明细”，列A:P，表头1行，采样384行；导入时逐点核对点号、段内点、0.3125ms时间间隔，以及A/B/C各相的地址、HEX、uint16和有符号AD一致性。内置数据存放在 `Samples/recorded-waveform-20261104.json`，程序启动校验18块地址/点数和整个寄存器序列摘要。

- 源xlsx文件SHA-256：`4b6f65229d45fb5c948221bea5030e42722c3a182ce318b3de85a29f230e568a`。
- 18块依次按大端拼接1152个uint16的SHA-256：`ed2e6e7c7dacecadddd42fbf84f380325d690733e6eddd15537c4749922acc43`。
- 首点A/B/C为 `0 / 1 / 433 AD`，末点为 `0 / 0 / -6 AD`。主要波形在C相，A/B相接近零。

原表不包含1552标定或故障时间，模拟器沿用现有1552=0x0104（框II/Rate=1.5）及模拟故障时间；它们不是从该Excel还原的实机参数。主图显示表内有符号AD，分块RMS(A)使用此模拟标定。


### 2026-11-04：硬件归一化后的RMS解释

用户确认硬件已完成N=64相关除法，软件按 `sqrt(sum(AD²))` 处理模拟器返回的有符号值，不再除N。模拟器内置的Excel/PDF寄存器值、源文件和寄存器序列SHA-256保持不变，不通过缩放样本抵消这次算法变更。

Excel样本第一段C相的64点平方和=24291643，当前AD-RMS=4928.655293282337；模拟框II、Rate=1.5时安培RMS=3220.922293348802，最终显示3220.9A。原402.6A为软件再次除64的旧口径，不再用作当前验收结果。公式及仍待同步的界面文字、导出公式和测试基准见[RMS说明](waveform-rms-explained.md)。
