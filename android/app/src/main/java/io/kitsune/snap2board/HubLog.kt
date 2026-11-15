package io.kitsune.snap2board

import android.util.Log
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object HubLog {
    private const val TAG = "Snap2Board"
    private val lines = ArrayDeque<String>()
    private val listeners = mutableSetOf<(String) -> Unit>()
    private val time = SimpleDateFormat("HH:mm:ss", Locale.US)

    @Synchronized
    fun log(message: String) {
        Log.i(TAG, message)
        val line = "${time.format(Date())} $message"
        lines.addLast(line)
        while (lines.size > 200) lines.removeFirst()
        listeners.toList().forEach { it(line) }
    }

    @Synchronized
    fun snapshot(): List<String> = lines.toList()

    @Synchronized
    fun clear() = lines.clear()

    @Synchronized
    fun addListener(listener: (String) -> Unit) {
        listeners.add(listener)
    }

    @Synchronized
    fun removeListener(listener: (String) -> Unit) {
        listeners.remove(listener)
    }
}
