package io.kitsune.snap2board

import android.util.Log
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

class HttpRequest(
    val method: String,
    val path: String,
    val query: Map<String, String>,
    val headers: Map<String, String>,
    val body: ByteArray
) {
    fun header(name: String): String? = headers[name.lowercase()]
}

class HttpResponse(
    val code: Int,
    val contentType: String,
    val length: Long,
    val writer: (OutputStream) -> Unit
) {
    companion object {
        fun json(code: Int, body: String): HttpResponse {
            val bytes = body.toByteArray(StandardCharsets.UTF_8)
            return HttpResponse(code, "application/json; charset=utf-8", bytes.size.toLong()) { it.write(bytes) }
        }

        fun bytes(code: Int, mime: String, data: ByteArray): HttpResponse =
            HttpResponse(code, mime, data.size.toLong()) { it.write(data) }

        fun stream(mime: String, length: Long, writer: (OutputStream) -> Unit): HttpResponse =
            HttpResponse(200, mime, length, writer)
    }
}

class MiniHttpServer(private val port: Int, private val handler: (HttpRequest) -> HttpResponse) {

    private var serverSocket: ServerSocket? = null

    @Volatile
    var running = false
        private set

    fun start() {
        val socket = ServerSocket(port)
        serverSocket = socket
        running = true
        Thread({ acceptLoop(socket) }, "s2b-accept").apply { isDaemon = true }.start()
    }

    fun stop() {
        running = false
        try {
            serverSocket?.close()
        } catch (_: Exception) {
        }
        serverSocket = null
    }

    private fun acceptLoop(socket: ServerSocket) {
        while (running) {
            val client = try {
                socket.accept()
            } catch (e: Exception) {
                if (running) Log.w("Snap2Board", "accept failed: ${e.message}")
                break
            }
            Thread({ serve(client) }, "s2b-conn").apply { isDaemon = true }.start()
        }
    }

    private fun serve(socket: Socket) {
        try {
            socket.soTimeout = 20000
            val input = BufferedInputStream(socket.getInputStream())
            val requestLine = readLine(input) ?: return
            val parts = requestLine.split(' ')
            if (parts.size < 2) return
            val method = parts[0].uppercase()
            val target = parts[1]

            val headers = mutableMapOf<String, String>()
            while (true) {
                val line = readLine(input) ?: break
                if (line.isEmpty()) break
                val colon = line.indexOf(':')
                if (colon > 0) {
                    headers[line.substring(0, colon).trim().lowercase()] = line.substring(colon + 1).trim()
                }
            }

            val contentLength = headers["content-length"]?.toLongOrNull() ?: 0L
            val body = if (contentLength > 0) {
                val data = ByteArray(contentLength.toInt())
                var offset = 0
                while (offset < data.size) {
                    val read = input.read(data, offset, data.size - offset)
                    if (read < 0) break
                    offset += read
                }
                data
            } else {
                ByteArray(0)
            }

            val question = target.indexOf('?')
            val path = if (question >= 0) target.substring(0, question) else target
            val query = if (question >= 0) parseQuery(target.substring(question + 1)) else emptyMap()

            val response = try {
                handler(HttpRequest(method, path, query, headers, body))
            } catch (t: Throwable) {
                Log.e("Snap2Board", "handler error", t)
                HttpResponse.json(500, "{\"error\":\"internal\"}")
            }
            writeResponse(socket.getOutputStream(), response)
        } catch (e: Exception) {
            Log.w("Snap2Board", "serve error: ${e.message}")
        } finally {
            try {
                socket.close()
            } catch (_: Exception) {
            }
        }
    }

    private fun writeResponse(out: OutputStream, response: HttpResponse) {
        val head = "HTTP/1.1 ${response.code} ${reason(response.code)}\r\n" +
            "Content-Type: ${response.contentType}\r\n" +
            "Content-Length: ${response.length}\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n\r\n"
        out.write(head.toByteArray(StandardCharsets.US_ASCII))
        response.writer(out)
        out.flush()
    }

    private fun reason(code: Int): String = when (code) {
        200 -> "OK"
        400 -> "Bad Request"
        401 -> "Unauthorized"
        403 -> "Forbidden"
        404 -> "Not Found"
        409 -> "Conflict"
        500 -> "Internal Server Error"
        else -> "OK"
    }

    private fun readLine(input: InputStream): String? {
        val buffer = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) return if (buffer.size() == 0) null else String(buffer.toByteArray(), StandardCharsets.UTF_8)
            if (b == '\n'.code) break
            if (b != '\r'.code) buffer.write(b)
            if (buffer.size() > 16384) return null
        }
        return String(buffer.toByteArray(), StandardCharsets.UTF_8)
    }

    private fun parseQuery(raw: String): Map<String, String> = raw.split('&')
        .filter { it.isNotEmpty() }
        .mapNotNull { pair ->
            val eq = pair.indexOf('=')
            if (eq < 0) null else URLDecoder.decode(pair.substring(0, eq), "UTF-8") to
                URLDecoder.decode(pair.substring(eq + 1), "UTF-8")
        }
        .toMap()
}
