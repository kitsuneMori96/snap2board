package io.kitsune.snap2board

import android.content.ContentResolver
import android.content.ContentUris
import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.provider.MediaStore
import java.security.MessageDigest

data class MediaPick(
    val id: Long,
    val uri: Uri,
    val name: String,
    val mime: String,
    val size: Long,
    val capturedAt: Long
)

object MediaPicks {

    private val BASE_PROJECTION = arrayOf(
        MediaStore.Images.Media._ID,
        MediaStore.Images.Media.DISPLAY_NAME,
        MediaStore.Images.Media.MIME_TYPE,
        MediaStore.Images.Media.SIZE,
        MediaStore.Images.Media.DATE_TAKEN,
        MediaStore.Images.Media.DATE_ADDED
    )

    fun latest(context: Context, screenshotsOnly: Boolean): MediaPick? {
        val selection = if (screenshotsOnly) "${MediaStore.Images.Media.RELATIVE_PATH} LIKE ?" else null
        val args = if (screenshotsOnly) arrayOf("%/Screenshots/%") else null
        context.contentResolver.query(
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            BASE_PROJECTION,
            selection,
            args,
            "${MediaStore.Images.Media.DATE_ADDED} DESC"
        )?.use { cursor ->
            if (!cursor.moveToFirst()) return null
            return read(cursor)
        }
        return null
    }

    /** 只返回 _id 大于水位、仍在白名单目录里、已写完的图片，按 _id 升序。 */
    fun queryNew(context: Context, sinceId: Long): List<MediaPick> {
        val projection = BASE_PROJECTION + MediaStore.MediaColumns.RELATIVE_PATH
        val selection = buildString {
            append("${MediaStore.Images.Media._ID} > ?")
            append(" AND (${MediaStore.MediaColumns.IS_PENDING} = 0 OR ${MediaStore.MediaColumns.IS_PENDING} IS NULL)")
            append(" AND (${MediaStore.MediaColumns.RELATIVE_PATH} LIKE ? OR ${MediaStore.MediaColumns.RELATIVE_PATH} LIKE ?)")
            append(" AND (${MediaStore.MediaColumns.OWNER_PACKAGE_NAME} IS NULL OR ${MediaStore.MediaColumns.OWNER_PACKAGE_NAME} != ?)")
        }
        val args = arrayOf(
            sinceId.toString(),
            // 魅族相机把照片直接落在 DCIM/ 根目录（P2026….jpg），不是 DCIM/Camera/
            "DCIM/%",
            "%/Screenshots/%",
            context.packageName
        )
        val result = mutableListOf<MediaPick>()
        context.contentResolver.query(
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            projection,
            selection,
            args,
            "${MediaStore.Images.Media._ID} ASC"
        )?.use { cursor ->
            while (cursor.moveToNext()) {
                result.add(read(cursor))
            }
        }
        return result
    }

    fun maxId(context: Context): Long {
        context.contentResolver.query(
            MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
            arrayOf(MediaStore.Images.Media._ID),
            null,
            null,
            "${MediaStore.Images.Media._ID} DESC"
        )?.use { cursor ->
            if (cursor.moveToFirst()) return cursor.getLong(0)
        }
        return 0L
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

    fun sha256(data: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(data).joinToString("") { "%02x".format(it) }

    private fun read(cursor: Cursor): MediaPick {
        val id = cursor.getLong(0)
        val name = cursor.getString(1) ?: "image"
        val mime = cursor.getString(2) ?: "image/jpeg"
        val size = cursor.getLong(3)
        val taken = if (cursor.isNull(4)) 0L else cursor.getLong(4)
        val added = cursor.getLong(5)
        val capturedAt = if (taken > 0) taken else added * 1000
        val uri = ContentUris.withAppendedId(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, id)
        return MediaPick(id, uri, name, mime, size, capturedAt)
    }
}
