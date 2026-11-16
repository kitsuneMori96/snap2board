package io.kitsune.snap2board

import android.util.Log
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.nio.charset.StandardCharsets
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

class HttpRequest(
    val method: String,
    val path: String,
    val query: Map<String, String>,
    val headers: Map<String, String>,
    val body: ByteArray
) {
    fun header(name: String): String? = headers[name.lowercase()]
}

/** forceClose：大文件流式响应后直接断连，避免长度不符时把下一条请求读串。 */
class HttpResponse(
    val code: Int,
    val contentType: String,
    val length: Long,
    val forceClose: Boolean = false,
    val writer: (OutputStream) -> Unit
) {
    companion object {
        fun json(code: Int, body: String): HttpResponse {
            val bytes = body.toByteArray(StandardCharsets.UTF_8)
            return HttpResponse(code, "application/json; charset=utf-8", bytes.size.toLong()) { it.write(bytes) }
        }

        fun bytes(code: Int, mime: String, data: ByteArray): HttpResponse =
            HttpResponse(code, mime, data.size.toLong()) { it.write(data) }

        fun stream(mime: String, length: Long, forceClose: Boolean = false, writer: (OutputStream) -> Unit): HttpResponse =
            HttpResponse(200, mime, length, forceClose, writer)
    }
}

class MiniHttpServer(private val port: Int, private val handler: (HttpRequest) -> HttpResponse) {

    private companion object {
        const val FIRST_REQUEST_TIMEOUT_MS = 20000
        const val KEEP_ALIVE_IDLE_MS = 15000
    }

    private var serverSocket: ServerSocket? = null
    private val workers = Executors.newCachedThreadPool { runnable ->
        Thread(runnable, "s2b-conn").apply { isDaemon = true }
    }
    private val clients = ConcurrentHashMap.newKeySet<Socket>()

    @Volatile
    var running = false
        private set

    fun start() {
        val socket = ServerSocket().apply {
            reuseAddress = true
            bind(InetSocketAddress("0.0.0.0", port), 64)
        }
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
        // 长连接不会随 ServerSocket 关闭而断，得逐个关掉才能让 worker 立刻退出
        clients.forEach { runCatching { it.close() } }
        clients.clear()
        workers.shutdownNow()
    }

    private fun acceptLoop(socket: ServerSocket) {
        while (running) {
            val client = try {
                socket.accept()
            } catch (e: Exception) {
                if (!running) break
                Log.w("Snap2Board", "accept failed: ${e.message}")
                try {
                    Thread.sleep(200)
                } catch (_: InterruptedException) {
                    break
                }
                continue
            }
            try {
                client.tcpNoDelay = true
                client.sendBufferSize = 256 * 1024
                clients.add(client)
                workers.execute { serve(client) }
            } catch (t: Throwable) {
                Log.w("Snap2Board", "派发连接失败: ${t.message}")
                clients.remove(client)
                runCatching { client.close() }
            }
        }
    }

    private fun serve(socket: Socket) {
        try {
            socket.soTimeout = FIRST_REQUEST_TIMEOUT_MS
            val input = BufferedInputStream(socket.getInputStream())
            val output = BufferedOutputStream(socket.getOutputStream())
            // PC 每 2 秒轮询一次 items，复用连接能省掉大量握手和 TIME_WAIT
            while (running) {
                val requestLine = readLine(input) ?: break
                val parts = requestLine.split(' ')
                if (parts.size < 2) break
                val method = parts[0].uppercase()
                val target = parts[1]

                val headers = mutableMapOf<String, String>()
                while (true) {
                    val line = readLine(input) ?: return
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

                var keepAlive = headers["connection"]?.equals("close", ignoreCase = true) != true
                if (response.forceClose) keepAlive = false
                writeResponse(output, response, keepAlive)
                if (!keepAlive) break
                socket.soTimeout = KEEP_ALIVE_IDLE_MS
            }
        } catch (e: Exception) {
            Log.w("Snap2Board", "serve error: ${e.message}")
        } finally {
            clients.remove(socket)
            try {
                socket.close()
            } catch (_: Exception) {
            }
        }
    }

    private fun writeResponse(out: OutputStream, response: HttpResponse, keepAlive: Boolean) {
        val head = "HTTP/1.1 ${response.code} ${reason(response.code)}\r\n" +
            "Content-Type: ${response.contentType}\r\n" +
            "Content-Length: ${response.length}\r\n" +
            "Connection: ${if (keepAlive) "keep-alive" else "close"}\r\n" +
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
        413 -> "Payload Too Large"
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
