# Snap2Board

手机拍的照 / 截的图，自动进电脑剪贴板；电脑剪的图，自动进手机相册。只在局域网里跑。

- 手机：魅族 18（Android 13，无 root，Shizuku 授权）
- 电脑：Surface Go（Windows，连手机热点）
- 拓扑：**手机开热点并充当 hub**，电脑只做出站连接，地址取默认网关

设计与协议见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 当前进度

M0 已跑通：手机前台服务内嵌 HTTP hub（配对 / 列表 / 下载），电脑托盘自动发现网关、轮询拉取并写入剪贴板（DIB + PNG + FileDrop 三种格式）。已在真实热点下验证。

M1 已跑通：MediaStore 自动监听（水位 + 1.5 s 防抖 + sha256 去重），手机截图/拍照后无需任何操作，约 2–4 s 内自动出现在电脑剪贴板。Shizuku 授权入口已接好，用于补全量媒体权限与保活白名单。

M2 已跑通：PC 复制图片后自动推到手机 `Pictures/Snap2Board` 相册；双向都有回显抑制，收到的图不会被再推回去。

M3 进行中：托盘历史（最近 10 张，可一键写回剪贴板）与断线补课（重连后只复制最新一张）已完成。

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

- M0 最小闭环：MediaStore 监听前的「手动发送 + 剪贴板」链路（已完成）
- M1 自动化上行：MediaStore 监听 + 水位去重 + Shizuku 授权（已完成）
- M2 反向：电脑剪贴板监听 + 推送到手机相册（已完成）
- M3 打磨：托盘历史、断线补课（已完成）；单实例、开机自启、TLS pinning、面板页

## 目录

```
android/     Kotlin 客户端（hub）
pc/          C# / .NET 10 托盘程序
protocol/    双端契约
docs/        设计文档
```
