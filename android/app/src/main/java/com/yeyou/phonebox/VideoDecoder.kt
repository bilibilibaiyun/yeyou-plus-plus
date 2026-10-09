package com.yeyou.phonebox

import android.media.MediaCodec
import android.media.MediaFormat
import android.view.Surface

/**
 * MediaCodec H.264 硬解，输出到 Surface（零拷贝）。
 *
 * 输入为 Annex-B 字节流（每个 NAL 带 00 00 00 01 起始码），SPS/PPS 随码流以起始码形式
 * 直接喂入，由解码器按「字节流（byte-stream）模式」从码流中解析。因此无需预先从 Config
 * 提取 SPS/PPS 再填 csd-0/csd-1——Android AVC 解码器在未显式设置 csd 时即按 Annex-B 解析。
 *
 * 输出通过 `configure(format, surface, null, 0)` 绑定到 SurfaceView，解码帧直接上屏。
 */
class VideoDecoder(private val surface: Surface) {

    private var codec: MediaCodec? = null
    private val bufferInfo = MediaCodec.BufferInfo()

    @Volatile
    private var started = false

    /**
     * 创建并启动解码器。
     *
     * @param width 视频宽（用于 MediaFormat；实际以码流 SPS 为准）
     * @param height 视频高
     * @return true=成功，false=启动失败
     */
    fun start(width: Int, height: Int): Boolean {
        if (started) {
            return true
        }

        return try {
            val format = MediaFormat.createVideoFormat(
                MediaFormat.MIMETYPE_VIDEO_AVC,
                if (width > 0) width else 1280,
                if (height > 0) height else 720
            )
            // 关键帧间隔、码率、SPS/PPS 等信息由码流提供，这里不强设 csd。
            val decoder = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            decoder.configure(format, surface, null, 0)
            decoder.start()

            codec = decoder
            started = true
            true
        } catch (e: Exception) {
            started = false
            codec = null
            false
        }
    }

    /**
     * 喂入一帧完整 H.264 数据（一个 access unit，含 SPS/PPS/IDR 等多个 Annex-B NAL，
     * 每个 NAL 带 00 00 00 01 起始码）。由 UDP 接收线程调用，一次性把整帧塞进一个
     * input buffer，内部同时完成输入入队与输出上屏，保持低延迟。
     *
     * 重要：用短超时重试等待输入缓冲，避免在缓冲暂不可用时把 SPS/PPS 这类
     * 关键 NAL 直接丢弃（否则首帧无法解码 → 黑屏）。仅重试若干次后仍无缓冲才放弃，
     * 以防 UDP 线程被无限阻塞。
     */
    fun feed(frame: ByteArray) {
        val decoder = codec ?: return
        if (!started) {
            return
        }

        try {
            var inIndex = -1
            var attempts = 0
            while (inIndex < 0 && attempts < 8) {
                inIndex = decoder.dequeueInputBuffer(5_000) // 5ms
                if (inIndex < 0) {
                    attempts++
                }
            }

            if (inIndex >= 0) {
                val inputBuffer = decoder.getInputBuffer(inIndex)
                if (inputBuffer != null && frame.size <= inputBuffer.capacity()) {
                    inputBuffer.clear()
                    inputBuffer.put(frame)
                    // PTS 统一为 0：直播投屏以「尽快上屏」为目标，不做回放时戳校准。
                    decoder.queueInputBuffer(inIndex, 0, frame.size, 0L, 0)
                }
            }
            drain(decoder)
        } catch (e: Exception) {
            // 单帧异常不终止解码。
        }
    }

    /** 取出解码输出并渲染到 Surface。 */
    private fun drain(decoder: MediaCodec) {
        while (true) {
            val outIndex = decoder.dequeueOutputBuffer(bufferInfo, 0)
            when {
                outIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> return
                outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    // Surface 模式下格式变化由 MediaCodec 自动处理。
                }
                outIndex == MediaCodec.INFO_OUTPUT_BUFFERS_CHANGED -> {
                    // 已废弃 API 的返回值，兼容性忽略。
                }
                outIndex >= 0 -> {
                    decoder.releaseOutputBuffer(outIndex, true)
                }
            }
        }
    }

    /** 停止并释放解码器。 */
    fun stop() {
        started = false
        val decoder = codec
        codec = null
        if (decoder != null) {
            try {
                decoder.stop()
            } catch (_: Exception) {
            }
            try {
                decoder.release()
            } catch (_: Exception) {
            }
        }
    }
}
