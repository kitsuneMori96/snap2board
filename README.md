# Snap2Board

手机拍的照 / 截的图，自动进电脑剪贴板；电脑剪的图，自动进手机相册。只在局域网里跑。

- 手机：魅族 18（Android 13，无 root，Shizuku 授权）
- 电脑：Surface Go（Windows，连手机热点）
- 拓扑：**手机开热点并充当 hub**，电脑只做出站连接，地址取默认网关

设计与协议见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 计划

- M0 最小闭环：内嵌 hub + 网关发现 + 剪贴板写入
- M1 自动化上行：MediaStore 监听 + 水位去重
- M2 反向：电脑剪贴板监听 + 手机相册插入
- M3 打磨：托盘历史、断线补课、TLS pinning

## 目录

```
android/     Kotlin 客户端（hub）
pc/          C# / .NET 8 托盘程序
protocol/    双端契约
docs/        设计文档
```
