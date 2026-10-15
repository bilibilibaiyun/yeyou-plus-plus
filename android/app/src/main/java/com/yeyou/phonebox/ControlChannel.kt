package com.yeyou.phonebox

import org.json.JSONObject
import java.nio.charset.StandardCharsets
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 基于中继隧道的控制通道客户端（手机端）。
 *
 * 中继隧道是单条双向字节流，控制消息（type=0x01）与视频帧（type=0x02）在同一流上多路复用，
 * 因此本类用一个统一的读线程按帧读取并分发：
 *   - type=0x01 → 解析控制消息，识别 Config（Type=2）后回调 [Listener.onConfig]；
 *   - type=0x02 → 触发 [videoFrameListener]，交给上层解码上屏。
 *
 * 发送：connect() 发 Hello（ClientType="android"），sendReady() 发 Ready，sendKey() 发 Key。
 */
class ControlChannel(private val tunnel: RelayTunnel) {

    companion object {
        const val TYPE_HELLO = 1
        const val TYPE_CONFIG = 2
        const val TYPE_READY = 3
        const val TYPE_KEY = 4
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

    /** 收到一帧完整 H.264 Annex-B 视频帧（type=0x02）。 */
    @Volatile
    var videoFrameListener: ((ByteArray) -> Unit)? = null

    private val running = AtomicBoolean(false)
    private var readerThread: Thread? = null

    /** 是否已连接（仅用于状态展示，非精确断言）。 */
    val isConnected: Boolean
        get() = running.get() && tunnel.isConnected

    /**
     * 发送 Hello 并启动统一读线程（隧道需已完成 join 配对）。
     * 发送失败时通过 [Listener.onError] 回调。
     */
    fun connect() {
        if (running.get()) {
            return
        }

        try {
            running.set(true)
            sendHello()

            readerThread = Thread({ readLoop() }, "PhoneBoxRelayDemux").apply {
                isDaemon = true
                start()
            }
            AppLog.i("ControlChannel", "已发送 Hello，读线程已启动")
        } catch (e: Exception) {
            AppLog.e("ControlChannel", "启动失败: ${e.message}", e)
            running.set(false)
            listener?.onError(e.message ?: "控制通道启动失败")
        }
    }

    /** 发送 Ready 消息（电脑端收到后开始推流）。 */
    fun sendReady() {
        val msg = JSONObject()
            .put("Type", TYPE_READY)
            .put("Ready", true)
        writeLine(msg.toString())
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
        writeLine(msg.toString())
    }

    /** 断开连接并停止读线程。 */
    fun disconnect() {
        running.set(false)
        tunnel.close()
        readerThread?.interrupt()
        readerThread = null
    }

    private fun sendHello() {
        val msg = JSONObject()
            .put("Type", TYPE_HELLO)
            .put("ClientType", "android")
            .put("Version", "1.0")
            .put("Token", "")
        writeLine(msg.toString())
    }

    private fun writeLine(line: String) {
        tunnel.writeLine(line)
    }

    /** 统一读线程：按帧读取并分发控制（0x01）与视频（0x02）。 */
    private fun readLoop() {
        try {
            while (running.get()) {
                val frame = tunnel.readFrame() ?: break
                val (type, data) = frame
                when (type) {
                    RelayTunnel.FRAME_TYPE_CONTROL -> handleControl(data)
                    RelayTunnel.FRAME_TYPE_VIDEO -> videoFrameListener?.invoke(data)
                    // 其他未知类型直接忽略。
                }
            }
        } catch (e: Exception) {
            if (running.get()) {
                AppLog.e("ControlChannel", "读取隧道失败", e)
                listener?.onError(e.message ?: "读取隧道失败")
            }
        } finally {
            if (running.getAndSet(false)) {
                listener?.onDisconnected()
            }
            // 复位连接状态（供下次重新 join）。
            tunnel.close()
        }
    }

    /** 解析控制消息。手机端只关心 Config（Type=2），其余类型忽略。 */
    private fun handleControl(data: ByteArray) {
        if (data.isEmpty()) {
            return
        }

        val line = String(data, StandardCharsets.UTF_8)
        if (line.isBlank()) {
            return
        }

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
                    // 手机端无需处理其他控制消息类型。
                }
            }
        } catch (e: Exception) {
            // 单条控制消息解析失败不影响连接，忽略即可。
        }
    }
}
