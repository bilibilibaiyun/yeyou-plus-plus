using System;
using System.Collections.Generic;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// H.264 Annex-B NALU → RTP 封装器。
    ///
    /// 输入为 Annex-B 字节流（00 00 01 / 00 00 00 01 起始码分隔的 NALU），
    /// 输出为完整 RTP 包字节数组（12 字节 RTP 头 + 载荷）。
    ///
    /// RTP 头（RFC 3550）字段：
    ///   字节0  V=2 | P=0 | X=0 | CC=0           → 0x80
    ///   字节1  M(1bit) | PT(7bit)               → PT=96（动态 H.264）
    ///   字节2-3  sequence number（大端，每包递增）
    ///   字节4-7  timestamp（大端，90kHz 时钟，每帧递增）
    ///   字节8-11 SSRC（随机，标识本流）
    ///
    /// 封包策略（RFC 6184）：
    ///   1) NALU 长度 &lt;= MTU（1200）时：单一 NALU 直接放入 RTP 载荷。
    ///   2) NALU 长度 &gt; MTU 时：拆成 FU-A 分片。每个分片载荷前加 2 字节：
    ///      FU indicator（F=0, NRI=NALU 的 NRI, Type=28）+ FU header
    ///      （S=首片, E=末片, R=0, Type=原 NALU 类型）。
    ///      因此每个分片可携带的 NALU 数据 = MTU - 2 = 1198 字节。
    /// </summary>
    public sealed class RtpPacketizer
    {
        /// <summary>RTP 载荷上限（字节）。</summary>
        public const int MaxPayloadSize = 1200;

        /// <summary>动态 H.264 载荷类型。</summary>
        public const int H264PayloadType = 96;

        /// <summary>FU-A 类型码（RFC 6184）。</summary>
        private const int FuAType = 28;

        /// <summary>90kHz 时钟下 30fps 的每帧时间戳增量。</summary>
        private const uint TimestampIncrement = 3000;

        private static readonly Random Random = new Random();

        private ushort sequenceNumber;
        private uint timestamp;
        private readonly uint ssrc;

        public RtpPacketizer()
        {
            // SSRC 取随机值，用于标识本推流会话；序列号/时间戳从 0 开始递增。
            ssrc = unchecked((uint)Random.Next());
            sequenceNumber = 0;
            timestamp = 0;
        }

        /// <summary>
        /// 把一帧 H.264 Annex-B 数据封装为 RTP 包列表。
        /// </summary>
        public List<byte[]> Packetize(byte[] h264Frame)
        {
            var packets = new List<byte[]>();
            foreach (var packet in EnumeratePackets(h264Frame))
            {
                packets.Add(packet);
            }
            return packets;
        }

        /// <summary>
        /// 逐包产出 RTP 包（延迟枚举，供推流循环按需消费）。
        /// 同一帧内所有包共享一个时间戳；时间戳在每帧开始时递增一次。
        /// </summary>
        public IEnumerable<byte[]> EnumeratePackets(byte[] h264Frame)
        {
            if (h264Frame == null)
            {
                throw new ArgumentNullException(nameof(h264Frame));
            }

            timestamp += TimestampIncrement;

            var nalus = SplitNalus(h264Frame);
            int count = nalus.Count;
            for (int i = 0; i < count; i++)
            {
                var nalu = nalus[i];
                // M 位标记「本帧的最后一个 RTP 包」：只有最后一个 NALU 的末包才置 M=1，
                // 让手机端据此区分 access unit 边界，把 SPS+PPS+IDR 作为一个完整帧喂给解码器。
                bool isLastNalu = i == count - 1;

                int header = nalu[0];
                int nri = (header >> 5) & 0x03;
                int nalType = header & 0x1F;

                if (nalu.Length <= MaxPayloadSize)
                {
                    // 单一 NALU：直接作为 RTP 载荷。
                    yield return BuildSinglePacket(nalu, nri, nalType, isLastNalu);
                }
                else
                {
                    // 超过 MTU：FU-A 分片。
                    foreach (var fragment in BuildFuAPackets(nalu, nri, nalType, isLastNalu))
                    {
                        yield return fragment;
                    }
                }
            }
        }

        /// <summary>
        /// 单一 NALU 封装：12 字节 RTP 头 + 完整 NALU。
        /// 仅当本 NALU 是本帧最后一个 NALU 时 M=1，否则 M=0。
        /// </summary>
        private byte[] BuildSinglePacket(byte[] nalu, int nri, int nalType, bool isLastNalu)
        {
            var packet = AllocatePacket(nalu.Length, marker: isLastNalu);
            Buffer.BlockCopy(nalu, 0, packet, 12, nalu.Length);
            return packet;
        }

        /// <summary>
        /// FU-A 分片：跳过 NALU 起始字节（NALU header），把剩余数据按 1198 字节切块。
        /// 首片 S=1，末片 E=1；仅「最后一个分片 && 本帧最后一个 NALU」时 M=1。
        /// </summary>
        private IEnumerable<byte[]> BuildFuAPackets(byte[] nalu, int nri, int nalType, bool isLastNalu)
        {
            // FU indicator：F=0，NRI 沿用原 NALU，Type=28(FU-A)。
            byte fuIndicator = (byte)((nri << 5) | FuAType);

            int offset = 1;             // 跳过 NALU header 字节
            int remaining = nalu.Length - 1;
            bool first = true;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, MaxPayloadSize - 2);
                bool last = chunk == remaining;
                bool isFrameEnd = last && isLastNalu;

                var packet = AllocatePacket(chunk + 2, isFrameEnd);

                packet[12] = fuIndicator;

                // FU header：S=首片(0x80)，E=末片(0x40)，R=0，Type=原 NALU 类型。
                byte fuHeader = (byte)((first ? 0x80 : 0x00) | (last ? 0x40 : 0x00) | nalType);
                packet[13] = fuHeader;

                Buffer.BlockCopy(nalu, offset, packet, 14, chunk);

                first = false;
                offset += chunk;
                remaining -= chunk;

                yield return packet;
            }
        }

        /// <summary>
        /// 分配 RTP 包并写入 12 字节 RTP 头，同时递增序列号。
        /// </summary>
        private byte[] AllocatePacket(int payloadLength, bool marker)
        {
            var packet = new byte[12 + payloadLength];

            packet[0] = 0x80; // V=2, P=0, X=0, CC=0
            packet[1] = (byte)((marker ? 0x80 : 0x00) | H264PayloadType);
            packet[2] = (byte)(sequenceNumber >> 8);
            packet[3] = (byte)(sequenceNumber & 0xFF);
            packet[4] = (byte)(timestamp >> 24);
            packet[5] = (byte)(timestamp >> 16);
            packet[6] = (byte)(timestamp >> 8);
            packet[7] = (byte)(timestamp & 0xFF);
            packet[8] = (byte)(ssrc >> 24);
            packet[9] = (byte)(ssrc >> 16);
            packet[10] = (byte)(ssrc >> 8);
            packet[11] = (byte)(ssrc & 0xFF);

            sequenceNumber++;

            return packet;
        }

        /// <summary>
        /// 从 Annex-B 字节流中切分 NALU。
        /// 正确识别 4 字节起始码 00 00 00 01 与 3 字节起始码 00 00 01：
        /// 检测到 00 00 01 时若其前紧邻 00，则起始码起点回退一位，
        /// 避免把 4 字节起始码的前导 00 错误计入上一个 NALU 末尾。
        /// </summary>
        private static List<byte[]> SplitNalus(byte[] data)
        {
            var nalus = new List<byte[]>();
            int n = data.Length;
            if (n < 4)
            {
                return nalus;
            }

            // 记录所有起始码起点（优先识别 4 字节 00 00 00 01）。
            var starts = new List<int>();
            int pos = 0;
            while (pos < n - 2)
            {
                if (data[pos] == 0x00 && data[pos + 1] == 0x00 && data[pos + 2] == 0x01)
                {
                    // 前面紧邻 0x00 即真实为 4 字节起始码，起点回退一位。
                    int start = (pos > 0 && data[pos - 1] == 0x00) ? pos - 1 : pos;
                    starts.Add(start);
                    pos += 3;
                }
                else
                {
                    pos++;
                }
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                // 4 字节起始码：data[start+2] == 0x00；否则为 3 字节起始码。
                int startCodeLength = (start + 3 < n && data[start + 2] == 0x00) ? 4 : 3;
                int payloadStart = start + startCodeLength;
                int payloadEnd = (i + 1 < starts.Count) ? starts[i + 1] : n;
                int length = payloadEnd - payloadStart;

                if (length > 0)
                {
                    var nalu = new byte[length];
                    Array.Copy(data, payloadStart, nalu, 0, length);
                    nalus.Add(nalu);
                }
            }

            return nalus;
        }
    }
}
