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
dotnet run --project src/WireLink.Simulator -- --port /tmp/wirelink-device --baud 9600 --address 1
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
dotnet run --project src/WireLink.Simulator -- --port COM11 --baud 9600 --address 1
```

主程序选择 COM10。正式部署应使用经过组织安全审查且与 Windows 版本兼容的虚拟 COM 驱动。

## 运行时命令

- `normal`：恢复正常。
- `timeout`：下一请求不响应；`timeout continuous`：持续不响应。
- `crc`：下一响应发送错误 CRC。
- `exception 02|03|04`：下一响应返回异常码。
- `disconnect`：关闭模拟器串口。
- `fault <type>`：提示使用主程序选择记录类型；故障/报警/变位及 0～15 均有样例。
- `status`：显示模式、地址和寄存器数。
- `quit`：退出。

模拟 uint32 始终按高字优先编码。电压、电流和电能会缓慢变化，寄存器 788 为 2。
