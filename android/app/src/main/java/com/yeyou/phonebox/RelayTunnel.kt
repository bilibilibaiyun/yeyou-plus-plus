package com.yeyou.phonebox

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.charset.StandardCharsets

/**
 * 中继服务器隧道客户端（手机端）。
 *
 * 中继服务器协议（纯文本行 + '\n'）：
 *   - 副设备：连 9000，发 "JOIN <8位数字>\n" → 收 "LINKED\n"（成功）/ "NOTFOUND\n" / "BUSY\n"。
 *   - 配对成功后，服务器在两端之间透明双向转发字节流（不解析内容）。
 *
 * 隧道帧格式（与桌面端一致）：
 *   每帧 = [1 字节类型][4 字节大端长度][数据]；
 *   0x01 = 控制消息（JSON）、0x02 = 视频帧（H.264 Annex-B 完整帧）。
 */
class RelayTunnel(
    private val host: String = DEFAULT_HOST,
    private val port: Int = DEFAULT_PORT
) {

    companion object {
        const val DEFAULT_HOST = "43.226.36.117"
        const val DEFAULT_PORT = 9000

        const val FRAME_TYPE_CONTROL = 0x01
        const val FRAME_TYPE_VIDEO = 0x02

        private const val CONNECT_TIMEOUT_MS = 10_000
        private const val MAX_FRAME_SIZE = 64 * 1024 * 1024
    }

    private val writeLock = Any()

    private var socket: Socket? = null
    private var input: DataInputStream? = null
    private var output: DataOutputStream? = null

    /** 是否已连接中继服务器。 */
    val isConnected: Boolean
        get() = socket?.let { it.isConnected && !it.isClosed } ?: false

    /** 建立到中继服务器的连接（幂等）。 */
    @Synchronized
    private fun ensureConnected() {
        if (isConnected) {
            return
        }

        val s = Socket()
        s.tcpNoDelay = true
        s.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        socket = s
        input = DataInputStream(s.getInputStream())
        output = DataOutputStream(s.getOutputStream())
        AppLog.i("RelayTunnel", "已连接中继服务器 $host:$port")
    }

    /**
     * 以副设备身份加入：发送 "JOIN <code>" 行并读回响应。
     * "LINKED" 成功；"NOTFOUND" / "BUSY" 或异常响应抛异常（信息友好）。
     */
    fun join(code: String) {
        if (code.isBlank()) {
            throw IllegalArgumentException("联机码不能为空")
        }

        ensureConnected()
        writeRawLine("JOIN $code")
        val line = readRawLine()

        if (line == "LINKED") {
            AppLog.i("RelayTunnel", "中继配对成功（联机码 $code）")
            return
        }

        // 配对失败：关闭连接以便下次重试。
        close()

        when {
            line == null -> throw IOException("中继服务器未响应")
            line == "NOTFOUND" -> throw IOException("未找到该联机码对应的主机，请确认联机码正确且主机已生成")
            line == "BUSY" -> throw IOException("该主机已被其他设备连接")
            else -> throw IOException("中继服务器返回异常：$line")
        }
    }

    /** 写入一帧隧道数据：[1 字节类型][4 字节大端长度][数据]。写操作串行化。 */
    fun writeFrame(type: Int, data: ByteArray) {
        synchronized(writeLock) {
            val out = output ?: throw IOException("隧道未连接")
            out.writeByte(type)
            out.writeInt(data.size) // DataOutputStream 写 4 字节大端
            out.write(data)
            out.flush()
        }
    }

    /** 读取一帧隧道数据。连接断开 / 读失败 / 非法长度返回 null。 */
    fun readFrame(): Pair<Int, ByteArray>? {
        val inp = input ?: return null
        return try {
            val type = inp.read()
            if (type < 0) {
                return null
            }

            val length = inp.readInt()
            if (length < 0 || length > MAX_FRAME_SIZE) {
                AppLog.w("RelayTunnel", "收到非法帧长度：$length")
                return null
            }

            val data = ByteArray(length)
            inp.readFully(data)
            type to data
        } catch (e: Exception) {
            AppLog.w("RelayTunnel", "读取隧道失败：${e.message}")
            null
        }
    }

    /** 发送一行控制消息（内部封装为 type=0x01 帧）。 */
    fun writeLine(line: String) {
        writeFrame(FRAME_TYPE_CONTROL, line.toByteArray(StandardCharsets.UTF_8))
    }

    /** 读取一行控制消息（内部从 type=0x01 帧解出）；非控制帧或失败返回 null。 */
    fun readLine(): String? {
        val frame = readFrame() ?: return null
        val (type, data) = frame
        if (type != FRAME_TYPE_CONTROL) {
            return null
        }
        return String(data, StandardCharsets.UTF_8)
    }

    /** 关闭隧道并释放 socket（幂等）。 */
    @Synchronized
    fun close() {
        try {
            output?.close()
        } catch (_: Exception) {
        }
        try {
            input?.close()
        } catch (_: Exception) {
        }
        try {
            socket?.close()
        } catch (_: Exception) {
        }

        output = null
        input = null
        socket = null
        AppLog.i("RelayTunnel", "中继隧道已关闭")
    }

    /** 发送一行纯文本到服务器（仅配对握手阶段使用）。 */
    private fun writeRawLine(line: String) {
        synchronized(writeLock) {
            val out = output ?: throw IOException("隧道未连接")
            out.write((line + "\n").toByteArray(StandardCharsets.US_ASCII))
            out.flush()
        }
    }

    /** 读取一行纯文本（仅配对握手阶段使用）。EOF 且未读到任何字节返回 null。 */
    private fun readRawLine(): String? {
        val inp = input ?: return null
        val sb = StringBuilder()
        while (true) {
            val c = inp.read()
            if (c < 0) {
                return if (sb.isEmpty()) null else sb.toString()
            }
            if (c == '\n'.code) {
                break
            }
            if (c != '\r'.code) {
                sb.append(c.toChar())
            }
        }
        return sb.toString()
    }
}
