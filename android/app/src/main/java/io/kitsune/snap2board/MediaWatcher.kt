package io.kitsune.snap2board

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.database.ContentObserver
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

/**
 * MediaStore 观察者。只用 _id 水位判断"新图片"，首次启动把水位顶到当前最大值，
 * 避免把历史相册一次性灌给电脑。
 */
class MediaWatcher(
    private val context: Context,
    private val prefs: Prefs,
    private val onPick: (MediaPick) -> Unit
) {
    private val lock = Any()
    private var observer: ContentObserver? = null
    private var scheduler: ScheduledExecutorService? = null
    private var pending: ScheduledFuture<*>? = null
    private var started = false

    fun start() {
        synchronized(lock) {
            if (started) return
            if (!hasMediaPermission()) {
                HubLog.log("自动监听未启动：缺少相册读取权限")
                return
            }
            if (prefs.mediaWatermark < 0) {
                val max = MediaPicks.maxId(context)
                prefs.mediaWatermark = max
                HubLog.log("首次启动：水位顶到 $max，历史图片不上传")
            }
            scheduler = Executors.newSingleThreadScheduledExecutor { runnable ->
                Thread(runnable, "s2b-watch").apply { isDaemon = true }
            }
            val contentObserver = object : ContentObserver(Handler(Looper.getMainLooper())) {
                override fun onChange(selfChange: Boolean) = scheduleDrain()
                override fun onChange(selfChange: Boolean, uri: Uri?) = scheduleDrain()
            }
            observer = contentObserver
            context.contentResolver.registerContentObserver(
                MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true, contentObserver
            )
            started = true
            HubLog.log("自动监听已开启，水位=${prefs.mediaWatermark}")
        }
    }

    fun stop() {
        synchronized(lock) {
            if (!started) return
            started = false
            pending?.cancel(false)
            pending = null
            observer?.let { context.contentResolver.unregisterContentObserver(it) }
            observer = null
            scheduler?.shutdown()
            scheduler = null
            HubLog.log("自动监听已停止")
        }
    }

    private fun scheduleDrain() {
        synchronized(lock) {
            if (!started) return
            pending?.cancel(false)
            pending = scheduler?.schedule({ drain() }, DEBOUNCE_MS, TimeUnit.MILLISECONDS)
        }
    }

    private fun drain() {
        if (!started) return
        try {
            val since = prefs.mediaWatermark
            val picks = MediaPicks.queryNew(context, since)
            if (picks.isEmpty()) return
            var failed = 0
            for (pick in picks) {
                try {
                    onPick(pick)
                } catch (t: Throwable) {
                    failed++
                    HubLog.log("处理 ${pick.name} 失败: ${t.message}")
                }
            }
            prefs.mediaWatermark = picks.last().id
            HubLog.log("检测到 ${picks.size} 张新图（失败 $failed），水位=${prefs.mediaWatermark}")
        } catch (t: Throwable) {
            HubLog.log("自动监听处理异常: ${t.message}")
        }
    }

    private fun hasMediaPermission(): Boolean {
        val permission = if (Build.VERSION.SDK_INT >= 33) {
            Manifest.permission.READ_MEDIA_IMAGES
        } else {
            Manifest.permission.READ_EXTERNAL_STORAGE
        }
        return context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED
    }

    companion object {
        private const val DEBOUNCE_MS = 1500L
    }
}
