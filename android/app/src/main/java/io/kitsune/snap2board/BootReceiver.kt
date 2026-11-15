package io.kitsune.snap2board

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return
        HubLog.log("开机自启：尝试拉起 hub")
        try {
            context.startForegroundService(Intent(context, HubService::class.java))
        } catch (t: Throwable) {
            HubLog.log("开机自启失败（可去 Flyme 自启动管理里放行）: ${t.message}")
        }
    }
}
