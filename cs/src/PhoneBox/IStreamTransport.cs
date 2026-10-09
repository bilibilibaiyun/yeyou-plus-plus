using System;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 视频流传输抽象接口。
    ///
    /// 本阶段由 <see cref="UdpRtpTransport"/> 实现（局域网 UDP RTP 直连）。
    /// 预留该抽象层是为了 P1 阶段跨网络投屏：后续只需新增一个实现同一接口的
    /// WebRtcTransport，即可在不改动 StreamingService / PhoneBoxServer 的情况下切换传输方式。
    /// </summary>
    public interface IStreamTransport : IDisposable
    {
        /// <summary>绑定本地端口并开始监听 / 准备发送。</summary>
        /// <param name="port">本地 UDP 端口，取值 <see cref="PhoneBoxConfig.UdpPort"/>。</param>
        void Start(int port);

        /// <summary>发送一个 RTP 包到目标远端。</summary>
        /// <param name="rtpPacket">完整 RTP 包（含 12 字节 RTP 头）。</param>
        /// <param name="length">有效字节数。</param>
        void SendRtp(byte[] rtpPacket, int length);

        /// <summary>停止传输，释放监听资源。</summary>
        void Stop();
    }
}
