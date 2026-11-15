package io.kitsune.snap2board

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class MainActivity : Activity() {

    private lateinit var statusView: TextView
    private lateinit var codeView: TextView
    private lateinit var shizukuView: TextView
    private lateinit var logView: TextView
    private val handler = Handler(Looper.getMainLooper())
    private val logListener: (String) -> Unit = { line ->
        runOnUiThread { appendLog(line) }
    }
    private val refreshTask = object : Runnable {
        override fun run() {
            refresh()
            handler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val density = resources.displayMetrics.density
        val padding = (16 * density).toInt()

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding, padding, padding)
        }

        statusView = TextView(this).apply {
            textSize = 14f
            setTextColor(Color.DKGRAY)
        }
        codeView = TextView(this).apply {
            textSize = 30f
            gravity = Gravity.CENTER
            setTypeface(typeface, Typeface.BOLD)
        }
        logView = TextView(this).apply {
            textSize = 11f
            setTextColor(Color.GRAY)
            setTextIsSelectable(true)
        }
        shizukuView = TextView(this).apply {
            textSize = 12f
            setTextColor(Color.DKGRAY)
        }
        val scroll = ScrollView(this).apply { addView(logView) }

        fun button(label: String, action: () -> Unit) = Button(this).apply {
            text = label
            setOnClickListener { action() }
        }

        root.addView(statusView)
        root.addView(codeView)
        root.addView(button("启动服务") { startHub() })
        root.addView(button("停止服务") { stopHub() })
        root.addView(button("申请权限") { requestMediaPermissions() })
        root.addView(shizukuView)
        root.addView(button("Shizuku 授权") {
            ShizukuGrants.ensurePermission(this) { msg ->
                HubLog.log(msg)
                refresh()
            }
        })
        root.addView(button("发送最新截图") { dispatch(HubService.ACTION_SEND_SCREENSHOT) })
        root.addView(button("发送最新照片") { dispatch(HubService.ACTION_SEND_PHOTO) })
        root.addView(button("重新生成配对码") {
            Prefs(this).regeneratePairCode()
            HubLog.log("配对码已重生成")
            refresh()
        })
        root.addView(button("清空日志") {
            HubLog.clear()
            logView.text = ""
        })
        root.addView(scroll, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))

        setContentView(root)
        HubLog.addListener(logListener)
        appendLog(HubLog.snapshot())
        requestMediaPermissions()
        startHub()
        handleIntent(intent)
        refresh()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        handler.post(refreshTask)
    }

    override fun onPause() {
        handler.removeCallbacks(refreshTask)
        super.onPause()
    }

    override fun onDestroy() {
        HubLog.removeListener(logListener)
        super.onDestroy()
    }

    private fun handleIntent(intent: Intent?) {
        when (intent?.action) {
            HubService.ACTION_SEND_SCREENSHOT, HubService.ACTION_SEND_PHOTO -> {
                dispatch(intent.action!!)
            }
        }
    }

    private fun startHub() {
        startForegroundService(Intent(this, HubService::class.java))
        refresh()
    }

    private fun stopHub() {
        stopService(Intent(this, HubService::class.java))
        refresh()
    }

    private fun dispatch(action: String) {
        val intent = Intent(this, HubService::class.java).setAction(action)
        startForegroundService(intent)
    }

    private fun requestMediaPermissions() {
        val missing = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= 33) {
            if (checkSelfPermission(Manifest.permission.READ_MEDIA_IMAGES) != PackageManager.PERMISSION_GRANTED) {
                missing.add(Manifest.permission.READ_MEDIA_IMAGES)
            }
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                missing.add(Manifest.permission.POST_NOTIFICATIONS)
            }
        } else if (checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.READ_EXTERNAL_STORAGE)
        }
        if (missing.isNotEmpty()) {
            requestPermissions(missing.toTypedArray(), 1)
        }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == ShizukuGrants.REQUEST_CODE) {
            ShizukuGrants.applyGrants(this) { msg ->
                HubLog.log(msg)
                refresh()
            }
        } else {
            if (HubService.running) startHub()
            refresh()
        }
    }

    private fun refresh() {
        val port = Prefs(this).port
        val running = HubService.running
        statusView.text = buildString {
            append(if (running) "服务运行中" else "服务未运行")
            append(" · 端口 $port")
            HubService.lastError?.let { append(" · 错误: $it") }
        }
        shizukuView.text = ShizukuGrants.status()
        codeView.text = Prefs(this).pairCode
    }

    private fun appendLog(lines: List<String>) {
        if (lines.isEmpty()) return
        logView.append(lines.joinToString("\n") + "\n")
    }

    private fun appendLog(line: String) {
        logView.append(line + "\n")
    }
}
