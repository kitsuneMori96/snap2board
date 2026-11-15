# Snap2Board

手机拍的照 / 截的图，自动进电脑剪贴板；电脑剪的图，自动进手机相册。只在局域网里跑。

- 手机：魅族 18（Android 13，无 root，Shizuku 授权）
- 电脑：Surface Go（Windows，连手机热点）
- 拓扑：**手机开热点并充当 hub**，电脑只做出站连接，地址取默认网关

设计与协议见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 当前进度

M0 已跑通：手机前台服务内嵌 HTTP hub（配对 / 列表 / 下载），电脑托盘自动发现网关、轮询拉取并写入剪贴板（DIB + PNG + FileDrop 三种格式）。已在真实热点下验证。

## 构建

```bash
# Android（需要 JDK 17 + Android SDK 36）
cd android && ./gradlew :app:assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk

# Windows
dotnet build pc/Snap2Board/Snap2Board.csproj -c Debug
pc/Snap2Board/bin/Debug/net10.0-windows/Snap2Board.exe --pair <手机上的6位码>
```

## 计划

- M1 自动化上行：MediaStore 监听 + 水位去重 + Shizuku 授权
- M2 反向：电脑剪贴板监听 + WS 推送 + 手机相册插入
- M3 打磨：托盘历史、断线补课、TLS pinning

## 目录

```
android/     Kotlin 客户端（hub）
pc/          C# / .NET 10 托盘程序
protocol/    双端契约
docs/        设计文档
```
