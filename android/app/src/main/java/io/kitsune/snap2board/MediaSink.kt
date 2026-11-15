package io.kitsune.snap2board

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.provider.MediaStore

/** 把 PC 推来的图落进系统相册（Pictures/Snap2Board），相册 App 里直接能看到。 */
object MediaSink {

    private const val ALBUM = "Pictures/Snap2Board"

    fun insertImage(context: Context, name: String, mime: String, data: ByteArray): Uri? {
        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.Images.Media.DISPLAY_NAME, name)
            put(MediaStore.Images.Media.MIME_TYPE, mime)
            put(MediaStore.Images.Media.RELATIVE_PATH, ALBUM)
            put(MediaStore.Images.Media.IS_PENDING, 1)
        }
        val uri = try {
            resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values)
        } catch (t: Throwable) {
            HubLog.log("相册插入失败: ${t.message}")
            null
        } ?: return null

        return try {
            resolver.openOutputStream(uri)?.use { it.write(data) }
                ?: throw IllegalStateException("openOutputStream 返回 null")
            values.clear()
            values.put(MediaStore.Images.Media.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            uri
        } catch (t: Throwable) {
            HubLog.log("相册写入失败: ${t.message}")
            runCatching { resolver.delete(uri, null, null) }
            null
        }
    }
}
