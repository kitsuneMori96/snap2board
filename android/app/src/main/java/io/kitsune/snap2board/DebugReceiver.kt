package io.kitsune.snap2board

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo

class DebugReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if ((context.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) == 0) {
            HubLog.log("忽略广播：release 构建不开放")
            return
        }
        val action = intent.action ?: return
        HubLog.log("广播触发 $action")
        val serviceIntent = Intent(context, HubService::class.java).setAction(action)
        try {
            if (HubService.running) {
                context.startService(serviceIntent)
            } else {
                context.startForegroundService(serviceIntent)
            }
        } catch (t: Throwable) {
            HubLog.log("广播启动服务失败: ${t.message}")
        }
    }
}
