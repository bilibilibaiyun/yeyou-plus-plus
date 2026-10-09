package com.yeyou.phonebox

import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.charset.StandardCharsets
import java.util.concurrent.atomic.AtomicBoolean

/**
 * TCP 控制通道（手机端客户端）。
 *
 * 与电脑端 `ControlChannel`（服务端）对齐：
 *   - 连接后发送 Hello（Type=1，含 Token）→ 等待 Config（Type=2）→ 发送 Ready（Type=3）
 *   - 之后可随时发送 Key（Type=4）
 *   - 每行一个 JSON，以 '\n' 结尾（换行分帧），字段名 PascalCase
 */
class ControlChannel(
    private val host: String,
    private val port: Int,
    private val token: String
) {

    companion object {
        const val TYPE_HELLO = 1
        const val TYPE_CONFIG = 2
        const val TYPE_READY = 3
        const val TYPE_KEY = 4

        private const val CONNECT_TIMEOUT_MS = 5_000
    }

    /** 电脑端下发的推流 / 按键布局配置。 */
    data class StreamConfig(
        val width: Int,
        val height: Int,
        val frameRate: Int,
        val bitrateKbps: Int,
        val keyLayout: String?
    )

    interface Listener {
        fun onConfig(config: StreamConfig)
        fun onError(message: String)
        fun onDisconnected()
    }

    @Volatile
    var listener: Listener? = null

    private val running = AtomicBoolean(false)
    private var socket: Socket? = null
    private var writer: OutputStreamWriter? = null
    private var readerThread: Thread? = null

    /** 是否已连接（仅用于状态展示，非精确断言）。 */
    val isConnected: Boolean
        get() {
            val s = socket ?: return false
            return s.isConnected && !s.isClosed
        }

    /**
     * 建立 TCP 连接，发送 Hello，并启动后台读线程等待 Config。
     * 连接 / 发送失败时通过 [Listener.onError] 回调（回调发生在当前调用线程）。
     */
    fun connect() {
        if (running.get()) {
            return
        }

        try {
            val s = Socket()
            s.tcpNoDelay = true
            s.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)

            socket = s
            writer = OutputStreamWriter(s.getOutputStream(), StandardCharsets.UTF_8)
            running.set(true)

            sendHello()

            readerThread = Thread({ readLoop() }, "PhoneBoxControlReader").apply {
                isDaemon = true
                start()
            }
        } catch (e: Exception) {
            closeSocket()
            listener?.onError(e.message ?: "TCP 连接失败")
        }
    }

    /** 发送 Ready 消息（电脑端收到后开始推流）。 */
    fun sendReady() {
        val msg = JSONObject()
            .put("Type", TYPE_READY)
            .put("Ready", true)
        sendLine(msg.toString())
    }

    /**
     * 发送按键消息。
     *
     * @param keyCode Windows 虚拟键码（Virtual-Key Code）
     * @param scanCode 硬件扫描码；虚拟按键无对应扫描码时填 0，由电脑端 MapVirtualKey 兜底
     * @param isDown true=按下，false=释放
     */
    fun sendKey(keyCode: Int, scanCode: Int, isDown: Boolean) {
        val msg = JSONObject()
            .put("Type", TYPE_KEY)
            .put("KeyCode", keyCode)
            .put("ScanCode", scanCode)
            .put("IsDown", isDown)
            .put("Timestamp", System.currentTimeMillis())
        sendLine(msg.toString())
    }

    /** 断开连接并停止读线程。 */
    fun disconnect() {
        running.set(false)
        closeSocket()
        readerThread?.interrupt()
        readerThread = null
    }

    private fun sendHello() {
        val msg = JSONObject()
            .put("Type", TYPE_HELLO)
            .put("ClientType", "android")
            .put("Version", "1.0")
            .put("Token", token)
        sendLine(msg.toString())
    }

    /** 发送一行 JSON（自动追加 '\n'）。写操作串行化，避免多线程交叠。 */
    private fun sendLine(line: String) {
        val w = writer ?: return
        synchronized(w) {
            w.write(line)
            w.write("\n")
            w.flush()
        }
    }

    /** 后台读线程：按行解析 JSON，识别 Config 消息并回调。 */
    private fun readLoop() {
        try {
            val s = socket ?: return
            val reader = BufferedReader(InputStreamReader(s.getInputStream(), StandardCharsets.UTF_8))
            var line: String?
            while (running.get()) {
                line = reader.readLine() ?: break
                if (line.isBlank()) {
                    continue
                }
                handleLine(line)
            }
        } catch (e: Exception) {
            if (running.get()) {
                listener?.onError(e.message ?: "读取控制通道失败")
            }
        } finally {
            if (running.getAndSet(false)) {
                listener?.onDisconnected()
            }
            closeSocket()
        }
    }

    /** 解析一行消息。手机端只关心 Config（Type=2），其余类型忽略。 */
    private fun handleLine(line: String) {
        try {
            val obj = JSONObject(line)
            when (obj.optInt("Type", -1)) {
                TYPE_CONFIG -> {
                    val config = StreamConfig(
                        width = obj.optInt("Width", 1280),
                        height = obj.optInt("Height", 720),
                        frameRate = obj.optInt("FrameRate", 30),
                        bitrateKbps = obj.optInt("BitrateKbps", 2500),
                        keyLayout = if (obj.has("KeyLayout")) obj.getString("KeyLayout") else null
                    )
                    listener?.onConfig(config)
                }
                else -> {
                    // 手机端无需处理其他消息类型。
                }
            }
        } catch (e: Exception) {
            // 单行 JSON 解析失败不影响连接，忽略即可。
        }
    }

    private fun closeSocket() {
        try {
            writer?.close()
        } catch (_: Exception) {
        }
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        writer = null
        socket = null
    }
}
