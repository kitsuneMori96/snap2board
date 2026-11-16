package io.kitsune.snap2board

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import org.json.JSONArray
import org.json.JSONObject
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class HubService : Service() {

    companion object {
        const val ACTION_SEND_SCREENSHOT = "io.kitsune.snap2board.SEND_LATEST_SCREENSHOT"
        const val ACTION_SEND_PHOTO = "io.kitsune.snap2board.SEND_LATEST_PHOTO"
        const val CHANNEL_ID = "hub"

        @Volatile
        var running = false
            private set

        @Volatile
        var lastError: String? = null

        private const val MAX_UPLOAD = 50L * 1024 * 1024
    }

    private lateinit var prefs: Prefs
    private lateinit var store: ItemStore
    private var server: MiniHttpServer? = null
    private var watcher: MediaWatcher? = null
    private val executor = Executors.newSingleThreadExecutor()
    private val watchdog = Executors.newSingleThreadScheduledExecutor { runnable ->
        Thread(runnable, "s2b-watchdog").apply { isDaemon = true }
    }
    private var probeFailures = 0

    override fun onCreate() {
        super.onCreate()
        prefs = Prefs(this)
        store = ItemStore(this)
        createChannel()
        val notification = buildNotification("正在启动")
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(1, notification)
        }
        if ((applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0) {
            HubLog.log("debug 配对码: ${prefs.pairCode}")
        }
        startServer()
        startWatcher()
        startWatchdog()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_SEND_SCREENSHOT -> sendLatest(true)
            ACTION_SEND_PHOTO -> sendLatest(false)
            else -> startWatcher()
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        watcher?.stop()
        watcher = null
        server?.stop()
        server = null
        running = false
        executor.shutdown()
        watchdog.shutdownNow()
        HubLog.log("服务停止")
        super.onDestroy()
    }

    private fun startWatcher() {
        if (watcher == null) {
            watcher = MediaWatcher(this, prefs) { pick -> autoCapture(pick) }
        }
        watcher?.start()
    }

    private fun autoCapture(pick: MediaPick) {
        val sha = MediaPicks.sha256(contentResolver, pick.uri)
        if (sha == null) {
            HubLog.log("自动读取失败: ${pick.name}")
            return
        }
        if (store.hasSha(sha)) {
            HubLog.log("跳过重复: ${pick.name}")
            return
        }
        val entry = store.add(pick, sha)
        HubLog.log("自动入队 #${entry.seq} ${pick.name} (${pick.size} bytes)")
        notify("已自动发送 #${entry.seq}")
    }

    private fun startWatchdog() {
        watchdog.scheduleWithFixedDelay(::probeSelf, 20, 20, TimeUnit.SECONDS)
    }

    /** 实测遇到过 listen 有积压、连接不处理的假死，用本地自检把它重启。 */
    private fun probeSelf() {
        if (!running) return
        val ok = try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress("127.0.0.1", prefs.port), 3000)
                socket.soTimeout = 3000
                socket.getOutputStream().apply {
                    write("GET /api/v1/health HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n".toByteArray())
                    flush()
                }
                socket.getInputStream().bufferedReader().readLine()?.contains(" 200 ") == true
            }
        } catch (t: Throwable) {
            false
        }
        if (ok) {
            probeFailures = 0
            return
        }
        probeFailures++
        HubLog.log("自检失败第 $probeFailures 次")
        if (probeFailures >= 2) {
            probeFailures = 0
            restartServer()
        }
    }

    private fun restartServer() {
        HubLog.log("hub 无响应，重启监听")
        server?.stop()
        server = null
        running = false
        try {
            startServer()
        } catch (t: Throwable) {
            HubLog.log("重启监听失败: ${t.message}")
        }
    }

    private fun startServer() {
        val port = prefs.port
        try {
            val instance = MiniHttpServer(port) { request -> route(request) }
            instance.start()
            server = instance
            running = true
            lastError = null
            HubLog.log("监听 0.0.0.0:$port，本机地址 ${localAddresses().joinToString(", ")}")
        } catch (t: Throwable) {
            running = false
            lastError = t.message
            HubLog.log("端口 $port 监听失败: ${t.message}")
        }
    }

    private fun route(request: HttpRequest): HttpResponse {
        if (request.method == "GET" && request.path == "/api/v1/health") {
            return HttpResponse.json(200, healthJson().toString())
        }
        if (request.method == "POST" && request.path == "/api/v1/pair/claim") {
            return claim(request)
        }
        if (!isAuthorized(request)) {
            return HttpResponse.json(401, JSONObject().put("error", "unauthorized").toString())
        }
        return when {
            request.method == "GET" && request.path == "/api/v1/items" -> items(request)
            request.method == "POST" && request.path == "/api/v1/upload" -> upload(request)
            request.method == "GET" && request.path.startsWith("/api/v1/items/") &&
                request.path.endsWith("/content") -> content(request)
            request.method == "GET" && request.path == "/" -> HttpResponse.bytes(
                200, "text/plain; charset=utf-8",
                "Snap2Board hub. 端点: /api/v1/health".toByteArray(StandardCharsets.UTF_8)
            )
            else -> HttpResponse.json(404, JSONObject().put("error", "not found").toString())
        }
    }

    private fun healthJson(): JSONObject = JSONObject().apply {
        put("service", "snap2board")
        put("version", BuildConfig.VERSION_NAME)
        put("device", Build.MODEL)
        put("port", prefs.port)
        put("hosts", JSONArray(localAddresses()))
        put("time", System.currentTimeMillis())
    }

    private fun claim(request: HttpRequest): HttpResponse {
        val body = try {
            JSONObject(String(request.body, StandardCharsets.UTF_8))
        } catch (t: Throwable) {
            return HttpResponse.json(400, JSONObject().put("error", "bad json").toString())
        }
        val code = body.optString("code")
        if (code != prefs.pairCode) {
            HubLog.log("配对失败，收到 code=$code")
            return HttpResponse.json(403, JSONObject().put("error", "bad code").toString())
        }
        HubLog.log("配对成功: ${body.optString("clientName", "pc")}")
        return HttpResponse.json(
            200,
            JSONObject().put("token", prefs.token).put("port", prefs.port).toString()
        )
    }

    private fun items(request: HttpRequest): HttpResponse {
        val since = request.query["since"]?.toLongOrNull() ?: 0L
        val array = JSONArray()
        store.since(since).forEach { array.put(it.toJson()) }
        val body = JSONObject().apply {
            put("items", array)
            put("latestSeq", store.latestSeq())
        }
        return HttpResponse.json(200, body.toString())
    }

    private fun upload(request: HttpRequest): HttpResponse {
        val mime = (request.header("content-type") ?: "image/png").substringBefore(';').trim().lowercase()
        if (!mime.startsWith("image/")) {
            return HttpResponse.json(400, JSONObject().put("error", "not an image").toString())
        }
        val data = request.body
        if (data.isEmpty()) {
            return HttpResponse.json(400, JSONObject().put("error", "empty body").toString())
        }
        if (data.size > MAX_UPLOAD) {
            return HttpResponse.json(413, JSONObject().put("error", "too large").toString())
        }
        val sha = request.header("x-sha256")?.lowercase()?.takeIf { it.length == 64 }
            ?: MediaPicks.sha256(data)
        if (store.hasSha(sha)) {
            HubLog.log("跳过重复上传 ${sha.take(12)}…")
            return HttpResponse.json(200, JSONObject().put("deduped", true).toString())
        }
        val capturedAt = request.header("x-captured-at")?.toLongOrNull() ?: System.currentTimeMillis()
        val name = request.header("x-name") ?: "snap2board-${capturedAt}${extensionFor(mime)}"
        val uri = MediaSink.insertImage(this, name, mime, data)
        if (uri == null) {
            return HttpResponse.json(500, JSONObject().put("error", "album write failed").toString())
        }
        val pick = MediaPick(0L, uri, name, mime, data.size.toLong(), capturedAt)
        val entry = store.add(pick, sha, origin = "windows")
        HubLog.log("收到 PC 图片 #${entry.seq} $name (${data.size} bytes)")
        notify("已存入相册 #${entry.seq}")
        return HttpResponse.json(
            200,
            JSONObject().put("seq", entry.seq).put("id", entry.id).put("deduped", false).toString()
        )
    }

    private fun extensionFor(mime: String): String = when (mime) {
        "image/png" -> ".png"
        "image/jpeg" -> ".jpg"
        "image/webp" -> ".webp"
        "image/gif" -> ".gif"
        else -> ".png"
    }

    private fun content(request: HttpRequest): HttpResponse {
        val id = request.path.split('/').getOrNull(4) ?: return notFound()
        val entry = store.byId(id) ?: return notFound()
        val uri = try {
            Uri.parse(entry.uri)
        } catch (t: Throwable) {
            return notFound()
        }
        val stream = try {
            contentResolver.openInputStream(uri)
        } catch (t: Throwable) {
            null
        } ?: return notFound()

        if (entry.size > 0) {
            return HttpResponse.stream(entry.mime, entry.size, forceClose = true) { output ->
                stream.use { input ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        output.write(buffer, 0, read)
                    }
                }
            }
        }
        val data = stream.use { it.readBytes() }
        return HttpResponse.bytes(200, entry.mime, data)
    }

    private fun notFound(): HttpResponse =
        HttpResponse.json(404, JSONObject().put("error", "not found").toString())

    private fun isAuthorized(request: HttpRequest): Boolean {
        val header = request.header("authorization") ?: return false
        return header == "Bearer ${prefs.token}"
    }

    private fun sendLatest(screenshotsOnly: Boolean) {
        HubLog.log("收到发送请求: ${if (screenshotsOnly) "最新截图" else "最新照片"}")
        executor.execute {
            try {
                val pick = MediaPicks.latest(this, screenshotsOnly)
                if (pick == null) {
                    HubLog.log("没有找到${if (screenshotsOnly) "截图" else "照片"}")
                    notify("没有找到${if (screenshotsOnly) "截图" else "照片"}")
                    return@execute
                }
                HubLog.log("读取 ${pick.name} (${pick.size} bytes)")
                val sha = MediaPicks.sha256(contentResolver, pick.uri)
                if (sha == null) {
                    HubLog.log("读取文件失败: ${pick.name}")
                    notify("读取失败")
                    return@execute
                }
                val entry = store.add(pick, sha)
                HubLog.log("已入队 #${entry.seq} ${sha.take(12)}… ${entry.name}")
                notify("已发送 #${entry.seq}")
            } catch (t: Throwable) {
                HubLog.log("发送异常: ${t.message}")
            }
        }
    }

    private fun notify(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(1, buildNotification(text))
    }

    private fun createChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Snap2Board", NotificationManager.IMPORTANCE_LOW)
        )
    }

    private fun buildNotification(text: String): Notification =
        Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Snap2Board")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .setOngoing(true)
            .build()

    private fun localAddresses(): List<String> {
        val result = mutableListOf<String>()
        try {
            for (nif in NetworkInterface.getNetworkInterfaces()) {
                if (!nif.isUp || nif.isLoopback) continue
                for (address in nif.inetAddresses) {
                    if (address is Inet4Address && !address.isLoopbackAddress) {
                        address.hostAddress?.let { result.add(it) }
                    }
                }
            }
        } catch (_: Exception) {
        }
        return result
    }
}
