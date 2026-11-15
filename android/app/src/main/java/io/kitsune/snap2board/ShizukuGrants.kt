package io.kitsune.snap2board

import android.content.Context
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import rikka.shizuku.Shizuku

/**
 * Shizuku 只用来补权限和保活白名单，业务逻辑不依赖它。
 * 未运行/未授权时全部降级为提示，不影响已有权限下的自动同步。
 */
object ShizukuGrants {

    const val REQUEST_CODE = 4242

    fun status(): String {
        return try {
            when {
                !Shizuku.pingBinder() -> "Shizuku 未运行"
                Shizuku.isPreV11() -> "Shizuku 版本过低"
                Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED -> "Shizuku 已授权"
                else -> "Shizuku 待授权"
            }
        } catch (t: Throwable) {
            "Shizuku 不可用: ${t.message}"
        }
    }

    fun ensurePermission(context: Context, onStatus: (String) -> Unit) {
        try {
            if (!Shizuku.pingBinder()) {
                onStatus("Shizuku 未运行，请先打开 Shizuku")
                return
            }
            if (Shizuku.isPreV11()) {
                onStatus("Shizuku 版本过低")
                return
            }
            if (Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED) {
                applyGrants(context, onStatus)
            } else {
                Shizuku.requestPermission(REQUEST_CODE)
                onStatus("已向 Shizuku 请求授权")
            }
        } catch (t: Throwable) {
            onStatus("Shizuku 调用失败: ${t.message}")
        }
    }

    fun applyGrants(context: Context, onStatus: (String) -> Unit) {
        Thread {
            val result = try {
                runCommands(context.packageName)
            } catch (t: Throwable) {
                "授权失败: ${t.message}"
            }
            Handler(Looper.getMainLooper()).post { onStatus(result) }
        }.apply { isDaemon = true }.start()
    }

    private fun newProcess(script: String): Process {
        // 13.x 里 newProcess 在 SDK 中被标为 private，只能反射拿
        val method = Shizuku::class.java.getDeclaredMethod(
            "newProcess",
            Array<String>::class.java,
            Array<String>::class.java,
            String::class.java
        )
        method.isAccessible = true
        return method.invoke(null, arrayOf("sh", "-c", script), null, null) as Process
    }

    private fun runCommands(pkg: String): String {
        val script = buildString {
            append("pm grant $pkg android.permission.READ_MEDIA_IMAGES 2>&1; ")
            append("pm grant $pkg android.permission.POST_NOTIFICATIONS 2>&1; ")
            append("dumpsys deviceidle whitelist +$pkg 2>&1")
        }
        val process = newProcess(script)
        val output = StringBuilder()
        val reader = Thread {
            try {
                process.inputStream.bufferedReader().forEachLine { output.appendLine(it) }
            } catch (_: Throwable) {
            }
        }.apply { isDaemon = true; start() }
        val errors = Thread {
            try {
                process.errorStream.bufferedReader().forEachLine { output.appendLine(it) }
            } catch (_: Throwable) {
            }
        }.apply { isDaemon = true; start() }
        val code = process.waitFor()
        reader.join(2000)
        errors.join(2000)
        val detail = output.toString().trim().replace("\n", " | ")
        val summary = if (detail.length > 200) detail.take(200) + "…" else detail
        HubLog.log("Shizuku 授权 exit=$code $summary")
        return if (code == 0) "Shizuku 授权完成" else "Shizuku 授权 exit=$code"
    }
}
