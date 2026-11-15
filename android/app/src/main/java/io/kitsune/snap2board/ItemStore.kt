package io.kitsune.snap2board

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.UUID

class ItemStore(context: Context) {

    data class Entry(
        val seq: Long,
        val id: String,
        val sha256: String,
        val mime: String,
        val size: Long,
        val capturedAt: Long,
        val name: String,
        val uri: String,
        val origin: String
    ) {
        fun toJson(): JSONObject = JSONObject().apply {
            put("seq", seq)
            put("id", id)
            put("sha256", sha256)
            put("mime", mime)
            put("size", size)
            put("capturedAt", capturedAt)
            put("name", name)
            put("uri", uri)
            put("origin", origin)
        }
    }

    private val file = File(context.filesDir, "items.json")
    private val items = mutableListOf<Entry>()
    private var nextSeq = 1L

    init {
        load()
    }

    @Synchronized
    fun add(pick: MediaPick, sha256: String): Entry {
        val entry = Entry(
            seq = nextSeq++,
            id = UUID.randomUUID().toString(),
            sha256 = sha256,
            mime = pick.mime,
            size = pick.size,
            capturedAt = pick.capturedAt,
            name = pick.name,
            uri = pick.uri.toString(),
            origin = "android"
        )
        items.add(entry)
        save()
        return entry
    }

    @Synchronized
    fun since(seq: Long): List<Entry> = items.filter { it.seq > seq }.sortedBy { it.seq }

    @Synchronized
    fun byId(id: String): Entry? = items.firstOrNull { it.id == id }

    @Synchronized
    fun hasSha(sha256: String): Boolean = items.any { it.sha256 == sha256 }

    @Synchronized
    fun latestSeq(): Long = items.lastOrNull()?.seq ?: 0L

    private fun load() {
        if (!file.exists()) return
        try {
            val obj = JSONObject(file.readText())
            nextSeq = obj.optLong("nextSeq", 1L)
            val array = obj.optJSONArray("items") ?: JSONArray()
            for (i in 0 until array.length()) {
                try {
                    val o = array.getJSONObject(i)
                    items.add(
                        Entry(
                            seq = o.getLong("seq"),
                            id = o.getString("id"),
                            sha256 = o.getString("sha256"),
                            mime = o.optString("mime", "image/jpeg"),
                            size = o.optLong("size"),
                            capturedAt = o.optLong("capturedAt"),
                            name = o.optString("name", "image"),
                            uri = o.optString("uri"),
                            origin = o.optString("origin", "android")
                        )
                    )
                } catch (t: Throwable) {
                    HubLog.log("跳过损坏条目 #$i: ${t.message}")
                }
            }
        } catch (t: Throwable) {
            HubLog.log("索引读取失败: ${t.message}")
        }
    }

    private fun save() {
        val array = JSONArray()
        items.forEach { array.put(it.toJson()) }
        val obj = JSONObject().apply {
            put("nextSeq", nextSeq)
            put("items", array)
        }
        val tmp = File(file.parentFile, "items.json.tmp")
        tmp.writeText(obj.toString())
        if (!tmp.renameTo(file)) {
            file.delete()
            tmp.renameTo(file)
        }
    }
}
