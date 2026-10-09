package com.yeyou.phonebox

import java.io.ByteArrayOutputStream

/**
 * RTP → H.264 Annex-B 拆包器（RFC 6184）。
 *
 * 电脑端 `RtpPacketizer` 的封装策略：
 *   - 小 NALU（<=1200 字节）单包直放：RTP 载荷 = 完整 NALU（含 NALU header）
 *   - 大 NALU 用 FU-A 分片：每片载荷 = [FU indicator][FU header][片段数据]
 *
 * 本类把它们还原成带起始码（00 00 00 01）的 Annex-B NAL 流，交给 MediaCodec 解码。
 */
class RtpDepacketizer {

    companion object {
        private const val RTP_HEADER_SIZE = 12
        private const val H264_PAYLOAD_TYPE = 96
        private const val FU_A_TYPE = 28

        /** Annex-B 起始码。 */
        private val START_CODE = byteArrayOf(0x00, 0x00, 0x00, 0x01)
    }

    /** FU-A 分片重组缓冲。 */
    private val fuBuffer = ByteArrayOutputStream()

    /** 正在重组的 NALU 的「重建后 NALU header」= (FU indicator & 0xE0) | (FU header 类型)。 */
    private var fuReconstructedHeader = 0

    private var fuActive = false

    /**
     * 处理一个 RTP 包。
     *
     * @param packet 完整 UDP 报文字节
     * @param offset 有效数据起始偏移
     * @param length 有效数据长度
     * @return 组装完成的单个 Annex-B NAL（含起始码）；尚未完成或无效包返回 null
     */
    fun process(packet: ByteArray, offset: Int, length: Int): ByteArray? {
        if (packet.size < offset + length || length < RTP_HEADER_SIZE) {
            return null
        }

        // 校验 RTP 版本（V=2）与动态 H.264 载荷类型（PT=96）。
        val first = packet[offset].toInt() and 0xFF
        val version = (first shr 6) and 0x03
        if (version != 2) {
            return null
        }
        val payloadType = packet[offset + 1].toInt() and 0x7F
        if (payloadType != H264_PAYLOAD_TYPE) {
            return null
        }

        val payloadLen = length - RTP_HEADER_SIZE
        if (payloadLen <= 0) {
            return null
        }

        val payloadOffset = offset + RTP_HEADER_SIZE
        val nalHeader = packet[payloadOffset].toInt() and 0xFF
        val nalType = nalHeader and 0x1F

        return when (nalType) {
            in 1..23 -> buildSingleNalu(packet, payloadOffset, payloadLen)
            FU_A_TYPE -> processFuA(packet, payloadOffset, payloadLen)
            // 电脑端不使用 STAP-A（24）等聚合包；收到未知类型直接忽略。
            else -> null
        }
    }

    /** 单 NALU：起始码 + 完整 NALU。 */
    private fun buildSingleNalu(packet: ByteArray, payloadOffset: Int, payloadLen: Int): ByteArray {
        val out = ByteArrayOutputStream(START_CODE.size + payloadLen)
        out.write(START_CODE)
        out.write(packet, payloadOffset, payloadLen)
        return out.toByteArray()
    }

    /**
     * FU-A 重组。
     * 载荷布局：[0]=FU indicator(F|NRI|28)，[1]=FU header(S|E|R|type)，[2..]=片段数据。
     */
    private fun processFuA(packet: ByteArray, payloadOffset: Int, payloadLen: Int): ByteArray? {
        if (payloadLen < 2) {
            return null
        }

        val fuIndicator = packet[payloadOffset].toInt() and 0xFF
        val fuHeader = packet[payloadOffset + 1].toInt() and 0xFF

        val isStart = (fuHeader and 0x80) != 0
        val isEnd = (fuHeader and 0x40) != 0
        val nalType = fuHeader and 0x1F

        if (isStart) {
            // 新分片开始：重建 NALU header（F + NRI 取自 FU indicator，类型取自 FU header）。
            fuBuffer.reset()
            fuReconstructedHeader = (fuIndicator and 0xE0) or nalType
            fuActive = true
        }

        if (!fuActive) {
            // 未收到首片就来了中/末片，丢弃。
            return null
        }

        val fragmentLen = payloadLen - 2
        if (fragmentLen > 0) {
            fuBuffer.write(packet, payloadOffset + 2, fragmentLen)
        }

        if (isEnd) {
            fuActive = false
            val out = ByteArrayOutputStream(START_CODE.size + 1 + fuBuffer.size())
            out.write(START_CODE)
            out.write(fuReconstructedHeader)
            fuBuffer.writeTo(out)
            return out.toByteArray()
        }

        return null
    }

    /** 丢弃未完成的分片（重连 / 停止时调用）。 */
    fun reset() {
        fuBuffer.reset()
        fuActive = false
    }
}
