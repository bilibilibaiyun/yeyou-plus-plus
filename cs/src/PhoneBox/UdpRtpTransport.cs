using System;
using System.Net;
using System.Net.Sockets;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 基于 UDP 的 RTP 发送实现（局域网直连）。
    ///
    /// UDP 是无连接协议，因此本类在发送前需要知道手机端的 IP:Port：
    /// 手机先通过 TCP 控制通道握手，握手成功后由 PhoneBoxServer 调用
    /// <see cref="SetRemote"/> 记录远端地址，之后 RTP 包全部发往该地址。
    /// </summary>
    public sealed class UdpRtpTransport : IStreamTransport
    {
        private readonly object sync = new object();
        private readonly PhoneBoxConfig config;

        private UdpClient udpClient;
        private IPEndPoint remote;
        private bool started;
        private bool disposed;

        public UdpRtpTransport(PhoneBoxConfig config)
        {
            this.config = config ?? new PhoneBoxConfig();
        }

        /// <summary>当前目标手机地址（未握手时为 null）。</summary>
        public IPEndPoint Remote
        {
            get
            {
                lock (sync)
                {
                    return remote;
                }
            }
        }

        /// <summary>是否已启动。</summary>
        public bool IsStarted
        {
            get
            {
                lock (sync)
                {
                    return started;
                }
            }
        }

        /// <summary>
        /// 记录目标手机地址（由控制通道握手成功后调用）。
        /// 约定：手机端 UDP 监听端口与电脑端一致（PhoneBoxConfig.UdpPort），IP 取控制通道对端 IP。
        /// </summary>
        public void SetRemote(IPEndPoint endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            lock (sync)
            {
                remote = endpoint;
            }
            PhoneBoxLog.Info("UDP RTP 目标已设置：" + endpoint);
        }

        /// <summary>绑定本地 UDP 端口。</summary>
        public void Start(int port)
        {
            lock (sync)
            {
                if (started || disposed)
                {
                    return;
                }

                udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                started = true;
            }
            PhoneBoxLog.Info("UDP RTP 传输已启动，本地端口 " + port + "。");
        }

        /// <summary>
        /// 发送单个 RTP 包。UDP 发送失败（网络瞬时异常等）吞掉异常并写日志，
        /// 绝不让单包失败中断推流循环。
        /// </summary>
        public void SendRtp(byte[] rtpPacket, int length)
        {
            if (rtpPacket == null || length <= 0 || length > rtpPacket.Length)
            {
                return;
            }

            UdpClient client;
            IPEndPoint target;

            lock (sync)
            {
                if (!started || disposed || remote == null)
                {
                    return;
                }
                client = udpClient;
                target = remote;
            }

            try
            {
                client.Send(rtpPacket, length, target);
            }
            catch (Exception ex)
            {
                // UDP 发送失败不影响后续帧，记录告警即可。
                PhoneBoxLog.Warn("UDP RTP 发送失败：" + ex.Message);
            }
        }

        /// <summary>停止发送并释放 socket。</summary>
        public void Stop()
        {
            lock (sync)
            {
                if (!started)
                {
                    return;
                }

                try
                {
                    if (udpClient != null)
                    {
                        udpClient.Close();
                    }
                }
                catch (Exception ex)
                {
                    PhoneBoxLog.Warn("UDP RTP socket 关闭失败：" + ex.Message);
                }

                udpClient = null;
                started = false;
                remote = null;
            }
            PhoneBoxLog.Info("UDP RTP 传输已停止。");
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            Stop();
            disposed = true;
        }
    }
}
