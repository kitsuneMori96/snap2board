# Snap2Board 架构设计

## 0. 场景与约束

| 项 | 值 |
|---|---|
| 手机 | 魅族 18，Flyme / Android 13，**无 root**，Shizuku 可用 |
| 电脑 | Surface Go，Windows x64 |
| 网络 | **手机开热点，Surface Go 连该热点**，仅局域网，不走云端 |
| 目标 | 手机拍照/截图 → 电脑剪贴板可直接 Ctrl+V；电脑截图 → 手机相册 |
| 交互 | 零操作自动同步（允许两端各有一个常驻进程） |

## 1. 拓扑：手机当 hub

原方案「PC 当 hub」在热点场景下不成立，原因很硬：

1. 热点里**手机是 AP 网关，地址稳定**；Surface Go 的地址由手机 DHCP 分配，重连就会变。
2. Surface Go 若当服务端，手机必须先找到「一个随时会变的 IP」。Android 在热点接口上做 mDNS 解析不可靠，等于要为发现机制造一套 ARP 扫描/广播协议。
3. 反过来，**Surface Go 读自己 Wi-Fi 网卡的默认网关，拿到的就是手机**，零配置、零发现协议。
4. Surface Go 只发起出站连接：Windows 防火墙在「公用网络」下不拦出站、也无需添加任何入站规则——热点网络默认会被 Windows 标记为公用网络，这一条省掉了一整类坑。
5. 手机开着热点时 Wi-Fi 射频本来就常亮，多一个监听 socket 的功耗可以忽略。

代价：hub 的状态（序列号、索引）落在手机上；Android 需要内嵌一个 HTTP 服务。这个代价远小于反向做发现，而且单客户端场景下一个 `ServerSocket` 就够了，不必引入框架。

```
┌──────────────────────── 魅族 18 (Hub, FGS) ────────────────────────┐
│  MiniHttpServer  0.0.0.0:28642  ── HTTP(下载/补课) + WS(M2 再加)      │
│      │                                                             │
│  ItemStore(seq 自增)  ◄── MediaWatcher(ContentObserver + 水位)       │
│      │                       └─ 相机 / 截图 新增                     │
│      ▼                                                             │
│  ReceiveSink ── MediaStore.insert(IS_PENDING) ──► 相册 DCIM/Snap2Board│
│                                                                    │
│  ShizukuGrants：pm grant READ_MEDIA_IMAGES / deviceidle whitelist    │
└───────────────────────────────▲────────────────────────────────────┘
                                │ 手机热点 AP（网关 192.168.43.x）
                                │ Surface 只做出站连接
┌───────────────────────────────┴──── Surface Go (Client) ────────────┐
│  HubClient ── 并发探测候选地址 /health → 轮询 since 游标                  │
│      │                                                             │
│      ├─► 下载图片 → 原子落盘 → ClipboardWriter(DIB+PNG+FileDrop)     │
│      └─► ClipboardWatcher(WM_CLIPBOARDUPDATE) → 回声表 → POST /items │
│  托盘：状态 / 历史 / 恢复上一份 / 配对 / 媒体文件夹                    │
└────────────────────────────────────────────────────────────────────┘
```

## 2. 组件

### 2.1 Android（Hub）

- **HubServer**（自研极简 HTTP，`MiniHttpServer.kt`）：绑定 `0.0.0.0:28642`，每连接一线程，只服务一个对端。M0 实测足够；M2 需要 WS 时再评估是否引入 Ktor，避免提前背依赖。
- **ItemStore**（Room）：`items(seq INTEGER PK AUTOINCREMENT, id, sha256 UNIQUE, mime, size, captured_at, origin, rel_path, local_uri)`。`seq` 是全局单调游标，PC 用它补课。
- **MediaWatcher**：`ContentResolver.registerContentObserver(Images.EXTERNAL_CONTENT_URI)`。触发后查询 `_id > watermark AND is_pending = 0 AND owner_package_name != 本包`，按 `_id` 升序处理，**先推进水位再处理**。
- **ReceiveSink**：`insert` → `IS_PENDING=1` → 写入 → `IS_PENDING=0`；`RELATIVE_PATH=DCIM/Snap2Board`，`DATE_TAKEN` 用 PC 端时间戳。该路径不需要任何存储权限。
- **FGS**：`dataSync` 类型，常驻通知显示「已连接 / PC 离线」。Android 13 没有 A15 的 6 小时限制。
- **ShizukuGrants**：仅一次性授权与 Shizuku 重启后的补刀，业务逻辑不依赖 Shizuku。
  - `pm grant <pkg> android.permission.READ_MEDIA_IMAGES`
  - `deviceidle whitelist +<pkg>`

### 2.2 Windows（Client）

- **GatewayDiscovery**：候选地址 = 上次可用地址 + 127.0.0.1 + 所有 IPv4 网卡的默认网关（去重），并发探测 `GET /health`（各 1.5 s 超时），应答且 `service == "snap2board"` 者即为 hub。托盘另提供「指定地址…」，兜底 USB 网络共享等非热点场景。
- **Diagnostics**：`--diag` 写 `%LOCALAPPDATA%\Snap2Board\diag.txt` 并打开记事本，列出网卡/网关/配置（不写 token）+ 逐个候选的探测结果，用于在陌生电脑上一分钟定位连不上的原因。
- **HubClient**：轮询 `GET /items?since=<cursor>` 补齐；跳过 `origin == windows` 的回声项，并在同一批内按 sha256 去重（手机端监听可能对同一 uri 重复入队）。
- **Store**：`%LOCALAPPDATA%\Snap2Board\media\<yyyyMM>\<sha256>.<ext>`，`.tmp` → rename 原子落盘。
- **ClipboardWriter**：独立 STA 线程 + 串行队列；`OpenClipboard` 失败重试 10×20ms。
- **ClipboardWatcher**：隐藏窗口 + `AddClipboardFormatListener`，收 `WM_CLIPBOARDUPDATE`。
- **Tray**：状态灯、最近 10 条、恢复上一份剪贴板、配对对话框、打开媒体文件夹、退出。
- **PanelForm**：托盘双击或菜单「面板…」打开的窗口（`--panel` 可直接带窗启动）。顶部显示连接状态 / hub 地址 / 游标 / 最近同步时间；中部历史列表带 64px 缩略图（等比缩放、不压扁竖屏截图），右侧「选中预览」按需解码原图（读完流再拷一份，不锁文件）；底部一排操作按钮（立即同步、恢复选中、配对、上传剪贴板、媒体文件夹）。1 s 定时刷新，关窗只是隐藏，托盘继续跑。
- **Outbox**：PC→手机方向断线时的磁盘队列。

## 3. 协议

```
GET  /api/v1/health                     → {service:"snap2board", version, device, hosts}
POST /api/v1/pair/claim   {code, clientName} → {token}
POST /api/v1/upload       body 即原始图片字节；Authorization: Bearer <token>
     Content-Type: image/png
     X-Sha256: <hex>   X-Captured-At: <ms>   X-Name: <文件名>
     → 200 {seq, id, deduped:false} | 200 {deduped:true} | 400 | 413 | 500
GET  /api/v1/items?since=<seq>          → {items:[{seq,id,sha256,mime,size,capturedAt,name,uri,origin}], latestSeq}
GET  /api/v1/items/{id}/content         → 原始字节
```

约定：

- **幂等键是 `sha256`**，重传无害；`seq` 只用于游标。
- 上行（手机→PC）走 `items?since=` 轮询；下行（PC→手机）走 `POST /upload` 直推，语义等价于「把这张图放进相册」。没做 WS：v1 的轮询延迟已经 2s 内，WS 只是锦上添花。
- **连接复用**：JSON 接口默认 `Connection: keep-alive`（空闲 15 s 断开），电脑 2 s 一次的轮询共用一条 TCP；`/content` 大图仍是一张一连接、响应后即关，避免长度不符时把下一条请求读串。
- `origin` 区分来源：`android`（手机产生）与 `windows`（PC 推过去的）。PC 拉到 `windows` 条目只推进游标，不写剪贴板。
- 配对：手机界面显示 6 位码，PC 托盘输入；PC 地址自动取网关，无需手填。
- 手机热点本身是 WPA2 加密 + token 鉴权，v1 走 HTTP 可接受；TLS（自签 + App 内 pinning）列入 v2。

## 4. 同步规则

- **首发不灌历史**：安装后首次启动把水位设为 `MAX(_id)`。`上传最近 24h` 作为可选开关，默认关。
- **路径白名单**：`DCIM/`（魅族相机直接存根目录，其他机型在 `DCIM/Camera/` 也覆盖）、`*/Screenshots/`，可配置。
- **防抖**：相机写盘会连发多次 UPDATE，回调合并 0.4s，并校验文件大小稳定后再读。
- **回声抑制三闸**（实测生效）：
  1. 手机侧查询带 `owner_package_name != 本包`，PC 推来的图由本包写入，永远不进上报队列；
  2. 手机侧路径白名单只认 `DCIM/` 与 `*/Screenshots/`，落进 `Pictures/Snap2Board/` 的图不在范围内；
  3. PC 侧按**像素哈希**（非 PNG 编码哈希）识别「这张图是 10 秒内我刚从手机写进剪贴板的」，直接跳过，避免把收到的图再推回去。
- **唤醒补课**：PC 休眠/断开期间产生的图片，连上后按 `since` 拉取，**只把最新一张写入剪贴板**（连续覆盖没有意义），其余进托盘历史（最近 10 张，`history.json` 持久化，点击即可重新写回剪贴板）。实测断开期间攒 3 张，重连后 1 秒内补齐并只复制最新一张。
- **游标推进时机**：下载成功并落盘后才推进 `cursor`，避免中途失败把条目永久跳过。唯一例外是源图返回 404（手机上已不可读）：记日志后跳过并推进游标，否则单张坏图会让整条链每 2 s 重试同一条、永远停在原处。
- **手机索引裁剪**：`items.json` 只保留最近 200 条，`seq` 单调不回收，PC 的 `since` 游标仍然有效。

## 5. 剪贴板写入（Windows 侧成败关键）

同时设置三种格式：

1. `CF_DIB` / PNG —— 微信、QQ、Word、浏览器；
2. `CF_HDROP` 指向落盘文件 —— 资源管理器、只认文件的程序；
3. 保留原文件路径，供托盘「在文件夹中显示」。

HEIC 风险：魅族相机若输出 HEIC，Windows 解码需要 HEIF 扩展。解码失败时降级为「只设 FileDrop + 托盘提示安装扩展」，不能让整条链断掉。PC→手机方向始终重新编码为 PNG，避免手机侧碰到奇怪格式。

## 6. 魅族 / Flyme 专项

- **权限**：`READ_MEDIA_IMAGES` 走 Shizuku `pm grant`（这是全量授权，不是 A14 的「部分照片」）。若被 ROM 打回，回退 `appops set <pkg> MANAGE_EXTERNAL_STORAGE allow` + FileObserver。
- **保活**：Flyme 后台管理严格。需要「允许自启动」「允许后台运行」「耗电管理=允许后台高耗电」，并把 App 和 Shizuku 都加入后台锁定（最近任务下拉锁定）。App 声明了 `BOOT_COMPLETED` 接收器，开机后自动拉起 hub，但 Flyme 会按「自启动管理」决定是否放行，必须手动允许一次。
- **Shizuku 本身**：Android 13 走无线调试启动；Flyme 可能杀 Shizuku 进程，同样加白名单。App 监听 Shizuku 状态变化并自动重跑 Grants。
- **截图目录**：Flyme 一般在 `Pictures/Screenshots`，白名单同时包含 `DCIM/Screenshots` 以防版本差异。
- **调试触发**：Flyme 的 `am` 在 `-a` 与 `-n` 同时出现时报 “Activity class does not exist”，隐式广播也会被后台限制。自动化统一走 `am broadcast -n io.kitsune.snap2board/.DebugReceiver -a io.kitsune.snap2board.SEND_LATEST_SCREENSHOT`；`DebugReceiver` 仅在 debuggable 构建里生效。
- **热点网段**：Flyme 热点不是 AOSP 默认的 192.168.43.x，本机实测为 192.168.147.x（手机=.177）。PC 端不要硬编码网段，一律读网关。
- **重启**：无 root，Shizuku 重启后需手动重开（无线调试），热点也需要手动开——这两件事本来就要做，App 随之恢复即可。
- **无线调试**：`settings put global adb_wifi_enabled 1` 在本机不生效（写完仍是 0，无 mDNS 广告、无 tls port），只能在「开发者选项 → 无线调试」手动打开；Shizuku 13 的「通过无线调试启动」同样需要界面上显示的配对码。
- **构建网络**：dl.google.com 在本网络不可直连，`settings.gradle.kts` 里 `mavenCentral()` 必须排在 `google()` 前面；Shizuku 依赖的 `androidx.annotation:1.3.0` 需手动经代理拉进 `~/.m2`（`mavenLocal()` 已加在最前）。

## 7. 失效模式

| 现象 | 对策 |
|---|---|
| Surface 重连热点后 IP 变化 | 无关：PC 只连网关 |
| 手机热点未开 / PC 连了别的 Wi-Fi | 探测所有网卡网关，全部失败则进「未连接」状态并退避重试；仍失败可手动指定 host:port |
| 手机 FGS 被杀 | 通知消失；Flyme 白名单 + 自启动；PC 侧检测到断连后持续重试 |
| PC 休眠 | 唤醒后重连 + `since` 补课，只补最新一张 |
| 剪贴板被其他程序占用 | Writer 队列 + OpenClipboard 重试，失败在托盘标红并保留历史 |
| HEIC 无法解码 | 降级 FileDrop + 提示 |
| 连拍产生重复回调 | 水位 + 防抖 + sha256 去重 |
| 手机 hub 假死（listen 有积压但连接永远不处理） | 实测遇到过一次：改用 `SO_REUSEADDR` + 线程池 + accept 失败重试，并加 20 s 本地自检，连续 2 次失败就重建监听 |
| PC 轮询线程静默退出 | `HttpClient` 超时抛的 `TaskCanceledException` 也是 `OperationCanceledException`，曾被当成「应用退出」而 break；现在只在 `ct.IsCancellationRequested` 时退出，其余记录超时继续重试，循环意外结束还会自拉起 |
| 手机流量/存储 | 默认仅图片；`Image.delete` 不处理（相册是用户的），App 只存索引 |

## 8. 默认参数

| 参数 | 默认值 |
|---|---|
| 端口 | 28642 |
| 单文件上限 | 50 MB |
| 视频 | 关闭 |
| 同步范围 | 相机新增 + 截图 |
| 首发补最近 24h | 关闭 |
| 哈希 | SHA-256 |
| 托盘历史 | 10 条，可恢复上一份剪贴板 |
| 重试 | 指数退避，上限 60s |
| 传输 | 仅 Wi-Fi（热点本身就是） |

## 9. 里程碑

- **M0 最小闭环**（已完成）：手机 FGS + 极简 HTTP `/health` `/pair/claim` `/items`；PC 网关发现 + 手动「发送最新截图」+ 剪贴板写入。已在 adb forward 与真实热点（网关 192.168.147.177）两条路径上验证。
- **M1 自动化上行**（已完成）：MediaWatcher + 水位 + 防抖 + 去重；PC 侧 `since` 拉取 + 自动写剪贴板。实测截图后约 2 s 手机入队、PC 侧 1.5 s 内拉取并写剪贴板（无任何手动触发）。
- **M2 反向**（已完成）：PC 剪贴板监听（`WM_CLIPBOARDUPDATE` + 250ms 防抖 + 剪贴板序号校验）→ `POST /upload` → 手机写入 `Pictures/Snap2Board`；回显由「像素哈希 + origin + owner 过滤」三重挡住。实测双向都在 2–3 s 内完成，且不会互相回弹。
- **M3 打磨**：托盘历史/恢复、断线补课、开机自启（PC 托盘开关 + 手机开机广播）、面板页已完成；剩余 TLS pinning。

## 10. 目录结构

```
snap2board/
├── android/            # Kotlin，单模块
│   └── app/src/main/java/.../  hub/ watch/ sink/ net/ db/ shizuku/ ui/
├── pc/Snap2Board/      # .NET 10 WinForms 托盘 + HttpClient
│   ├── Program.cs  Discovery/  HubClient/  Clipboard/  Store/  Tray/
├── protocol/           # 双端共享的 JSON schema 与契约测试样例
└── docs/
```
