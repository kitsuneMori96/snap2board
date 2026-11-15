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
import java.net.NetworkInterface
import java.nio.charset.StandardCharsets
import java.util.concurrent.Executors

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
    }

    private lateinit var prefs: Prefs
    private lateinit var store: ItemStore
    private var server: MiniHttpServer? = null
    private var watcher: MediaWatcher? = null
    private val executor = Executors.newSingleThreadExecutor()

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
        put("version", "0.1.0")
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
            return HttpResponse.stream(entry.mime, entry.size) { output ->
                stream.use { it.copyTo(output) }
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
