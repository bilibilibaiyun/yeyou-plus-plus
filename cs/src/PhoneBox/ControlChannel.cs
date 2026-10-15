using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 电脑端控制 / 按键通道（服务端，基于中继隧道帧传输）。
    ///
    /// 通过 <see cref="RelayTunnel"/> 与副设备通信：控制消息统一封装为 type=0x01 帧
    /// （每帧载荷为一个紧凑 JSON，字段名 PascalCase），复用 PhoneBoxProtocol 做序列化 / 反序列化。
    ///
    /// 消息处理：
    ///   HelloMessage → 回 ConfigMessage（分辨率 / 帧率 / 码率 / 按键布局）
    ///   ReadyMessage → 触发 SessionReady 事件（上层开始推流）
    ///   KeyMessage   → 注入本机按键，并触发 KeyReceived 事件（上层展示反馈）
    ///
    /// 消息接收在独立后台线程完成，不阻塞 UI / 推流。Start() 在隧道已建立后启动消息循环。
    /// </summary>
    public sealed class ControlChannel : IDisposable
    {
        private readonly object sync = new object();
        private readonly PhoneBoxConfig config;
        private readonly RelayTunnel tunnel;

        private Thread receiveThread;
        private volatile bool running;

        public ControlChannel(PhoneBoxConfig config, RelayTunnel tunnel)
        {
            this.config = config ?? new PhoneBoxConfig();
            this.tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
        }

        /// <summary>副设备已连接（收到 Hello 握手消息）。</summary>
        public event Action ClientConnected;

        /// <summary>副设备连接断开。</summary>
        public event Action ClientDisconnected;

        /// <summary>收到副设备 Hello 握手消息。</summary>
        public event Action<HelloMessage> HelloReceived;

        /// <summary>副设备发送 Ready，会话就绪，可开始推流。</summary>
        public event Action SessionReady;

        /// <summary>收到副设备按键消息（已注入本机后抛出，供 UI 展示反馈）。</summary>
        public event Action<KeyMessage> KeyReceived;

        /// <summary>
        /// 口令校验器（可选）：收到 Hello 后调用，返回 false 时拒绝该连接。
        /// 中继配对已由服务器 CODE 鉴权，通常无需再注入口令校验。
        /// </summary>
        public Func<string, bool> TokenValidator { get; set; }

        /// <summary>消息循环是否运行中。</summary>
        public bool IsRunning
        {
            get { return running; }
        }

        /// <summary>启动消息循环（隧道需已通过 SendHost 建立）。</summary>
        public void Start()
        {
            lock (sync)
            {
                if (running)
                {
                    return;
                }
                running = true;

                receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "PhoneBoxControlRelay"
                };
                receiveThread.Start();
            }
            PhoneBoxLog.Info("控制通道已启动（中继帧传输）。");
        }

        /// <summary>停止消息循环并关闭隧道。</summary>
        public void Stop()
        {
            Thread t;
            lock (sync)
            {
                if (!running)
                {
                    return;
                }
                running = false;
                t = receiveThread;
                receiveThread = null;
            }

            // 关闭隧道使阻塞中的 ReadLine 退出。
            tunnel.Close();

            if (t != null && t != Thread.CurrentThread)
            {
                t.Join(2000);
            }
            PhoneBoxLog.Info("控制通道已停止。");
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>消息接收循环：按帧读取 type=0x01 控制消息并分发。</summary>
        private void ReceiveLoop()
        {
            try
            {
                while (running)
                {
                    string line = tunnel.ReadLine();
                    if (line == null)
                    {
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    if (!ProcessLine(line))
                    {
                        break; // 鉴权失败等需要断开连接的情况。
                    }
                }
            }
            catch (Exception ex)
            {
                if (running)
                {
                    PhoneBoxLog.Warn("控制通道会话异常：" + ex.Message);
                }
            }
            finally
            {
                // 无论主动停止还是远端断开，都关闭隧道以复位连接状态（供下次重新 SendHost）。
                tunnel.Close();
                RaiseClientDisconnected();
            }
        }

        /// <summary>解析并分发一行 JSON 消息。返回 false 表示需要断开连接。</summary>
        private bool ProcessLine(string line)
        {
            object message;
            try
            {
                message = ParseMessage(line);
            }
            catch (JsonException ex)
            {
                PhoneBoxLog.Warn("控制通道 JSON 解析失败：" + ex.Message);
                return true;
            }

            var hello = message as HelloMessage;
            if (hello != null)
            {
                if (TokenValidator != null && !TokenValidator(hello.Token))
                {
                    PhoneBoxLog.Warn("副设备握手口令校验失败，连接被拒绝。");
                    return false;
                }
                SendConfig();
                RaiseClientConnected();
                RaiseHelloReceived(hello);
                return true;
            }

            if (message is ReadyMessage)
            {
                RaiseSessionReady();
                return true;
            }

            var key = message as KeyMessage;
            if (key != null)
            {
                InjectKey(key);
                RaiseKeyReceived(key);
                return true;
            }

            PhoneBoxLog.Warn("收到未知控制消息：" + line);
            return true;
        }

        /// <summary>
        /// 把一行 JSON 解析为对应消息对象：先读取 Type 字段，再反序列化到对应 DTO。
        /// </summary>
        private static object ParseMessage(string json)
        {
            var obj = JObject.Parse(json);
            var typeToken = obj["Type"];
            if (typeToken == null)
            {
                return null;
            }

            int type = typeToken.Value<int>();
            switch (type)
            {
                case (int)MessageType.Hello:
                    return obj.ToObject<HelloMessage>();
                case (int)MessageType.Config:
                    return obj.ToObject<ConfigMessage>();
                case (int)MessageType.Ready:
                    return obj.ToObject<ReadyMessage>();
                case (int)MessageType.Key:
                    return obj.ToObject<KeyMessage>();
                default:
                    return null;
            }
        }

        /// <summary>回应 ConfigMessage（分辨率 / 帧率 / 码率 / 按键布局）。</summary>
        private void SendConfig()
        {
            var message = new ConfigMessage
            {
                Width = config.Width,
                Height = config.Height,
                FrameRate = config.FrameRate,
                BitrateKbps = config.BitrateKbps,
                KeyLayout = config.KeyLayout
            };

            tunnel.WriteLine(PhoneBoxProtocol.Serialize(message));
        }

        /// <summary>
        /// 注入副设备上报的按键：优先使用上报的扫描码；缺失时由虚拟键码换算扫描码。
        /// 双人输入隔离：副设备按键统一用扫描码注入（KEYEVENTF_SCANCODE）。
        /// </summary>
        private static void InjectKey(KeyMessage message)
        {
            ushort scanCode = message.ScanCode > 0
                ? (ushort)message.ScanCode
                : RemoteInputSimulator.ScanCodeFromVirtualKey(message.KeyCode);

            if (scanCode == 0)
            {
                PhoneBoxLog.Warn(
                    "收到无法映射的按键消息：KeyCode=" + message.KeyCode
                    + "，ScanCode=" + message.ScanCode + "。");
                return;
            }

            if (message.IsDown)
            {
                RemoteInputSimulator.KeyDown(scanCode);
            }
            else
            {
                RemoteInputSimulator.KeyUp(scanCode);
            }
        }

        private void RaiseClientConnected()
        {
            var handler = ClientConnected;
            if (handler != null)
            {
                handler();
            }
        }

        private void RaiseClientDisconnected()
        {
            var handler = ClientDisconnected;
            if (handler != null)
            {
                handler();
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
