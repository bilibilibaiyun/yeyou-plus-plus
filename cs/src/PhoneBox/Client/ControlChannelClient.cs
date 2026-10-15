using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using YeyouPlusPlus.PhoneBox;

namespace YeyouPlusPlus.PhoneBox.Client
{
    /// <summary>
    /// 基于中继隧道的控制通道客户端（从机侧）。
    ///
    /// 中继隧道是单条双向字节流，控制消息（type=0x01）与视频帧（type=0x02）在同一流上多路复用，
    /// 因此本类用一个统一的读线程按帧读取并分发：
    ///   - type=0x01 → 解析控制消息，识别 Config 后触发 <see cref="ConfigReceived"/>；
    ///   - type=0x02 → 触发 <see cref="VideoFrameReceived"/>，交给上层解码上屏。
    ///
    /// 发送：Connect() 发 Hello，SendReady() 发 Ready，SendKey() 发 Key。
    /// </summary>
    public sealed class ControlChannelClient : IDisposable
    {
        private const string ClientType = "desktop";
        private const string ClientVersion = "1.0";

        private readonly object sync = new object();
        private readonly RelayTunnel tunnel;

        private Thread readerThread;
        private volatile bool running;

        /// <summary>收到主机下发的 Config 消息（推流 / 按键布局配置）。</summary>
        public event Action<ConfigMessage> ConfigReceived;

        /// <summary>连接 / 读取发生错误（参数为错误描述）。</summary>
        public event Action<string> Error;

        /// <summary>连接断开。</summary>
        public event Action Disconnected;

        /// <summary>收到一帧完整 H.264 Annex-B 视频帧（type=0x02）。</summary>
        public event Action<byte[]> VideoFrameReceived;

        public ControlChannelClient(RelayTunnel tunnel)
        {
            this.tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        }

        /// <summary>消息循环是否运行中。</summary>
        public bool IsRunning
        {
            get { return running; }
        }

        /// <summary>发送 Hello 并启动统一读线程（隧道需已完成 Join 配对）。</summary>
        public void Connect()
        {
            lock (sync)
            {
                if (running)
                {
                    return;
                }
                running = true;
            }

            SendHello();

            readerThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "PhoneBoxClientDemux"
            };
            readerThread.Start();

            PhoneBoxLog.Info("从机控制通道已启动（已发送 Hello）。");
        }

        /// <summary>发送 Ready 消息（主机收到后开始推流）。</summary>
        public void SendReady()
        {
            tunnel.WriteLine(PhoneBoxProtocol.Serialize(new ReadyMessage { Ready = true }));
        }

        /// <summary>发送按键消息。scanCode 为 0 时由主机端 MapVirtualKey 兜底换算扫描码。</summary>
        public void SendKey(int keyCode, int scanCode, bool isDown)
        {
            var message = new KeyMessage
            {
                KeyCode = keyCode,
                ScanCode = scanCode,
                IsDown = isDown,
                Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };
            tunnel.WriteLine(PhoneBoxProtocol.Serialize(message));
        }

        /// <summary>停止读线程并关闭隧道。</summary>
        public void Disconnect()
        {
            Thread t;
            lock (sync)
            {
                if (!running)
                {
                    return;
                }
                running = false;
                t = readerThread;
                readerThread = null;
            }

            tunnel.Close();

            if (t != null && t != Thread.CurrentThread)
            {
                t.Join(2000);
            }
        }

        public void Dispose()
        {
            Disconnect();
        }

        private void SendHello()
        {
            var message = new HelloMessage
            {
                ClientType = ClientType,
                Version = ClientVersion,
                Token = string.Empty
            };
            tunnel.WriteLine(PhoneBoxProtocol.Serialize(message));
        }

        /// <summary>统一读线程：按帧读取并分发控制（0x01）与视频（0x02）。</summary>
        private void ReadLoop()
        {
            try
            {
                while (running)
                {
                    byte type;
                    byte[] data;
                    if (!tunnel.ReadFrame(out type, out data))
                    {
                        break;
                    }

                    if (type == RelayTunnel.FrameTypeControl)
                    {
                        HandleControl(data);
                    }
                    else if (type == RelayTunnel.FrameTypeVideo)
                    {
                        RaiseVideoFrameReceived(data);
                    }
                    // 其他未知类型直接忽略。
                }
            }
            catch (Exception ex)
            {
                if (running)
                {
                    PhoneBoxLog.Warn("从机隧道读取异常：" + ex.Message);
                    RaiseError(ex.Message);
                }
            }
            finally
            {
                bool wasRunning;
                lock (sync)
                {
                    wasRunning = running;
                    running = false;
                }
                if (wasRunning)
                {
                    RaiseDisconnected();
                }
            }
        }

        /// <summary>解析控制消息。从机只关心 Config（Type=2），其余类型忽略。</summary>
        private void HandleControl(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            string line = Encoding.UTF8.GetString(data);
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            try
            {
                var obj = JObject.Parse(line);
                int type = obj.Value<int?>("Type") ?? -1;
                if (type != (int)MessageType.Config)
                {
                    return;
                }

                RaiseConfigReceived(obj.ToObject<ConfigMessage>());
            }
            catch (Exception ex)
            {
                // 单条控制消息解析失败不影响连接，忽略即可。
                PhoneBoxLog.Warn("从机解析控制消息失败：" + ex.Message);
            }
        }

        private void RaiseConfigReceived(ConfigMessage config)
        {
            var handler = ConfigReceived;
            if (handler != null)
            {
                handler(config);
            }
        }

        private void RaiseVideoFrameReceived(byte[] frame)
        {
            var handler = VideoFrameReceived;
            if (handler != null)
            {
                handler(frame);
            }
        }

        private void RaiseError(string message)
        {
            var handler = Error;
            if (handler != null)
            {
                handler(message);
            }
        }

        private void RaiseDisconnected()
        {
            var handler = Disconnected;
            if (handler != null)
            {
                handler();
            }
        }
    }
}
