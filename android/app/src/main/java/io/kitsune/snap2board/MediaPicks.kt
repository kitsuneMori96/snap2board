package io.kitsune.snap2board

import android.content.ContentResolver
import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import java.security.MessageDigest

data class MediaPick(
    val uri: Uri,
    val name: String,
    val mime: String,
    val size: Long,
    val capturedAt: Long
)

object MediaPicks {

    fun latest(context: Context, screenshotsOnly: Boolean): MediaPick? {
        val projection = arrayOf(
            MediaStore.Images.Media._ID,
            MediaStore.Images.Media.DISPLAY_NAME,
            MediaStore.Images.Media.MIME_TYPE,
            MediaStore.Images.Media.SIZE,
            MediaStore.Images.Media.DATE_TAKEN,
            MediaStore.Images.Media.DATE_ADDED
        )
        val selection = if (screenshotsOnly) "${MediaStore.Images.Media.RELATIVE_PATH} LIKE ?" else null
        val args = if (screenshotsOnly) arrayOf("%Screenshots%") else null
        context.contentResolver.query(
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            projection,
            selection,
            args,
            "${MediaStore.Images.Media.DATE_ADDED} DESC"
        )?.use { cursor ->
            if (!cursor.moveToFirst()) return null
            val id = cursor.getLong(0)
            val name = cursor.getString(1) ?: "image"
            val mime = cursor.getString(2) ?: "image/jpeg"
            val size = cursor.getLong(3)
            val taken = if (cursor.isNull(4)) 0L else cursor.getLong(4)
            val added = cursor.getLong(5)
            val capturedAt = if (taken > 0) taken else added * 1000
            val uri = ContentUris.withAppendedId(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, id)
            return MediaPick(uri, name, mime, size, capturedAt)
        }
        return null
    }

    fun sha256(resolver: ContentResolver, uri: Uri): String? {
        val digest = MessageDigest.getInstance("SHA-256")
        val stream = try {
            resolver.openInputStream(uri)
        } catch (t: Throwable) {
            null
        } ?: return null
        stream.use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
