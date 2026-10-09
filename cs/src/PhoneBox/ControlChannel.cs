using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 电脑端 TCP 控制 / 按键通道（服务端）。
    ///
    /// 监听 PhoneBoxConfig.TcpPort，接受手机连接后按行分帧接收 JSON 消息
    /// （每行一个 JSON，以 '\n' 结尾），复用 PhoneBoxProtocol 做序列化 / 反序列化。
    ///
    /// 消息处理：
    ///   HelloMessage → 回 ConfigMessage（分辨率 / 帧率 / 码率 / 按键布局）
    ///   ReadyMessage → 标记会话就绪，触发 SessionReady 事件（上层开始推流）
    ///   KeyMessage   → 触发 KeyReceived 事件（上层注入按键）
    ///
    /// 消息接收在独立后台线程完成，不阻塞 UI / 推流。
    /// </summary>
    public sealed class ControlChannel : IDisposable
    {
        private readonly object sync = new object();
        private readonly PhoneBoxConfig config;

        private TcpListener listener;
        private Thread acceptThread;
        private TcpClient currentClient;
        private volatile bool running;

        public ControlChannel(PhoneBoxConfig config)
        {
            this.config = config ?? new PhoneBoxConfig();
        }

        /// <summary>手机 TCP 连接建立（参数为手机端 TCP 地址）。</summary>
        public event Action<IPEndPoint> ClientConnected;

        /// <summary>手机 TCP 连接断开（参数为断开前的手机端地址）。</summary>
        public event Action<IPEndPoint> ClientDisconnected;

        /// <summary>收到手机 Hello 握手消息。</summary>
        public event Action<HelloMessage> HelloReceived;

        /// <summary>手机发送 Ready，会话就绪，可开始推流。</summary>
        public event Action SessionReady;

        /// <summary>收到手机按键消息。</summary>
        public event Action<KeyMessage> KeyReceived;

        /// <summary>是否正在监听。</summary>
        public bool IsRunning
        {
            get { return running; }
        }

        /// <summary>启动监听，开始接受手机连接。</summary>
        public void Start()
        {
            lock (sync)
            {
                if (running)
                {
                    return;
                }

                listener = new TcpListener(IPAddress.Any, config.TcpPort);
                listener.Start();
                running = true;

                acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "PhoneBoxControlAccept"
                };
                acceptThread.Start();
            }
            PhoneBoxLog.Info("TCP 控制通道已监听端口 " + config.TcpPort + "。");
        }

        /// <summary>停止监听并断开当前手机连接。</summary>
        public void Stop()
        {
            lock (sync)
            {
                if (!running)
                {
                    return;
                }
                running = false;

                try
                {
                    if (listener != null)
                    {
                        listener.Stop();
                    }
                }
                catch (Exception ex)
                {
                    PhoneBoxLog.Warn("控制通道监听停止失败：" + ex.Message);
                }

                CloseCurrentClient();
            }
            PhoneBoxLog.Info("TCP 控制通道已停止。");
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>接受循环：每接受一个连接，派一个后台线程处理。</summary>
        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    // listener 被 Stop() 关闭时正常退出。
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PhoneBoxLog.Warn("接受手机连接失败：" + ex.Message);
                    continue;
                }

                // 同一时刻只保留一个手机会话：新连接到达时先断开旧连接。
                lock (sync)
                {
                    CloseCurrentClient();
                }

                var thread = new Thread(() => HandleClient(client))
                {
                    IsBackground = true,
                    Name = "PhoneBoxControlClient"
                };
                thread.Start();
            }
        }

        /// <summary>处理单个手机连接的消息循环。</summary>
        private void HandleClient(TcpClient client)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;

            lock (sync)
            {
                currentClient = client;
            }

            NetworkStream stream = null;
            try
            {
                stream = client.GetStream();
                RaiseClientConnected(remote);

                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    string line;
                    while (running && (line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }
                        ProcessLine(line, stream);
                    }
                }
            }
            catch (Exception ex)
            {
                if (running)
                {
                    PhoneBoxLog.Warn("控制通道会话异常断开：" + ex.Message);
                }
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(currentClient, client))
                    {
                        currentClient = null;
                    }
                }
                client.Close();
                RaiseClientDisconnected(remote);
            }
        }

        /// <summary>解析并分发一行 JSON 消息。</summary>
        private void ProcessLine(string line, NetworkStream stream)
        {
            object message;
            try
            {
                message = ParseMessage(line);
            }
            catch (JsonException ex)
            {
                PhoneBoxLog.Warn("控制通道 JSON 解析失败：" + ex.Message);
                return;
            }

            var hello = message as HelloMessage;
            if (hello != null)
            {
                SendConfig(stream);
                RaiseHelloReceived(hello);
                return;
            }

            if (message is ReadyMessage)
            {
                RaiseSessionReady();
                return;
            }

            var key = message as KeyMessage;
            if (key != null)
            {
                RaiseKeyReceived(key);
                return;
            }

            PhoneBoxLog.Warn("收到未知控制消息：" + line);
        }

        /// <summary>
        /// 把一行 JSON 解析为对应消息对象。
        /// 协议消息未带显式 type 字段，因此按特征字段区分类型。
        /// </summary>
        private static object ParseMessage(string json)
        {
            var obj = JObject.Parse(json);

            if (obj.Property("ClientType") != null || obj.Property("Version") != null)
            {
                return obj.ToObject<HelloMessage>();
            }
            if (obj.Property("Ready") != null)
            {
                return obj.ToObject<ReadyMessage>();
            }
            if (obj.Property("KeyCode") != null || obj.Property("IsDown") != null
                || obj.Property("ScanCode") != null)
            {
                return obj.ToObject<KeyMessage>();
            }

            return null;
        }

        /// <summary>回应 ConfigMessage（分辨率 / 帧率 / 码率 / 按键布局）。</summary>
        private void SendConfig(NetworkStream stream)
        {
            var message = new ConfigMessage
            {
                Width = config.Width,
                Height = config.Height,
                FrameRate = config.FrameRate,
                BitrateKbps = config.BitrateKbps,
                KeyLayout = config.KeyLayout
            };

            SendLine(stream, PhoneBoxProtocol.Serialize(message));
        }

        /// <summary>发送一行文本（追加 '\n' 作为分帧符）。</summary>
        private static void SendLine(NetworkStream stream, string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private void CloseCurrentClient()
        {
            if (currentClient == null)
            {
                return;
            }
            try
            {
                currentClient.Close();
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Warn("关闭旧手机连接失败：" + ex.Message);
            }
            currentClient = null;
        }

        private void RaiseClientConnected(IPEndPoint remote)
        {
            var handler = ClientConnected;
            if (handler != null)
            {
                handler(remote);
            }
        }

        private void RaiseClientDisconnected(IPEndPoint remote)
        {
            var handler = ClientDisconnected;
            if (handler != null)
            {
                handler(remote);
            }
        }

        private void RaiseHelloReceived(HelloMessage hello)
        {
            var handler = HelloReceived;
            if (handler != null)
            {
                handler(hello);
            }
        }

        private void RaiseSessionReady()
        {
            var handler = SessionReady;
            if (handler != null)
            {
                handler();
            }
        }

        private void RaiseKeyReceived(KeyMessage key)
        {
            var handler = KeyReceived;
            if (handler != null)
            {
                handler(key);
            }
        }
    }
}
