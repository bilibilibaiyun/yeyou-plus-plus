package com.yeyou.phonebox

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * UDP RTP 接收器：监听端口 8761，收包 → 拆包 → 喂给解码器。
 *
 * 电脑端约定（`PhoneBoxServer.OnClientConnected`）：UDP 目标 = TCP 对端 IP + UDP 端口 8761。
 * 因此手机端绑定 0.0.0.0:8761 即可收到局域网电脑发来的 RTP 包。
 */
class UdpStreamReceiver(
    private val port: Int,
    private val depacketizer: RtpDepacketizer,
    private val decoder: VideoDecoder
) {

    interface Listener {
        fun onError(message: String)
    }

    @Volatile
    var listener: Listener? = null

    private val running = AtomicBoolean(false)
    private var socket: DatagramSocket? = null
    private var thread: Thread? = null

    /** 启动后台接收线程。 */
    fun start() {
        if (running.getAndSet(true)) {
            return
        }
        thread = Thread({ runLoop() }, "PhoneBoxUdpReceiver").apply {
            isDaemon = true
            start()
        }
    }

    /** 停止接收：先关闭 socket 使阻塞中的 receive 抛异常退出，再打断线程。 */
    fun stop() {
        running.set(false)
        socket?.close()
        thread?.interrupt()
        thread = null
    }

    private fun runLoop() {
        try {
            // 绑定 0.0.0.0:port，接受来自局域网电脑的 RTP 包。
            val s = DatagramSocket(port)
            socket = s
            try {
                s.receiveBufferSize = 1 shl 20 // 1 MB，尽量减小丢包。
            } catch (_: Exception) {
            }

            // UDP 单包最大载荷（受 IPv4 总长约束的保守上限）。
            val buffer = ByteArray(65507)
            val packet = DatagramPacket(buffer, buffer.size)

            while (running.get()) {
                try {
                    packet.length = buffer.size
                    s.receive(packet)

                    val nalu = depacketizer.process(packet.data, packet.offset, packet.length)
                    if (nalu != null) {
                        decoder.feed(nalu)
                    }
                } catch (e: Exception) {
                    if (running.get()) {
                        listener?.onError(e.message ?: "UDP 接收异常")
                    }
                }
            }
        } catch (e: Exception) {
            if (running.get()) {
                listener?.onError(e.message ?: "UDP 监听失败")
            }
        } finally {
            socket?.close()
            socket = null
        }
    }
}
