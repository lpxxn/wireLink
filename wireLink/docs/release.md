# 发布、签名与串口权限

## 自包含便携发布

```bash
dotnet publish src/WireLink.App -c Release -r win-x64 --self-contained true -o publish/app-win-x64
dotnet publish src/WireLink.App -c Release -r linux-x64 --self-contained true -o publish/app-linux-x64
dotnet publish src/WireLink.App -c Release -r osx-arm64 --self-contained true -o publish/app-osx-arm64
dotnet publish src/WireLink.Simulator -c Release -r win-x64 --self-contained true -o publish/simulator-win-x64
dotnet publish src/WireLink.Simulator -c Release -r osx-arm64 --self-contained true -o publish/simulator-osx-arm64
```


```
windows权限问题
$env:AVALONIA_TELEMETRY_OPTOUT = "1"

 Remove-Item "publish/app-win-x64" -Recurse -Force -ErrorAction SilentlyContinue

 dotnet publish src/WireLink.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/app-win-x64
```

正式应用包不复制 Simulator。跨 OS publish 能生成文件，但发布前应在目标 OS 启动和连接串口验证。

## macOS 26 调试签名

部分 NuGet 本机库的供应商签名与调试 apphost 临时签名不一致，系统可能拒绝加载。仅用于本机开发的处理：

```bash
codesign --force --sign - src/WireLink.App/bin/Debug/net10.0/runtimes/osx/native/libSkiaSharp.dylib
codesign --force --sign - src/WireLink.App/bin/Debug/net10.0/WireLink.App
```

每次重新构建可能覆盖文件，需要重签。生产发布应生成 `.app`，从内层 dylib/framework 到主程序统一使用 Developer ID 签名，再公证和 stapling；不要把临时签名当作正式交付。

## 串口权限

- Windows：关闭占用 COM 的工具；设备管理器确认端口号和驱动。
- macOS：端口通常为 `/dev/cu.*`；优先使用 `cu` 而非 `tty`；检查驱动和系统扩展授权。
- Linux：将用户加入串口组（常见为 `dialout`），重新登录；确认 `/dev/ttyUSB*` 权限。不要长期用 root 运行应用。

端口占用、权限不足、线缆断开和串口消失会在顶部提示及日志中记录。应用不会自动重连，需手动重新打开并连接测试。
