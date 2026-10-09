using System;
using System.Net;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 手机盒子服务入口：负责编排 TCP 控制通道与 UDP 推流传输，维护会话状态。
    ///
    /// 生命周期：Start() 启动 ControlChannel（TCP 8760）与 UdpRtpTransport（UDP 8761），
    /// Stop() 依次停止二者并复位会话。通过事件把「手机已连接 / 手机就绪 / 收到按键」
    /// 上抛给 UI 层（MainWindow 后续订阅）。
    ///
    /// 推流本体由 <see cref="StreamingService"/> 负责，UI 层在收到 PhoneReady 事件后创建
    /// 并启动 StreamingService（本阶段编码器未接入，仅框架）。
    /// </summary>
    public sealed class PhoneBoxServer : IDisposable
    {
        private readonly PhoneBoxConfig config;
        private readonly PhoneBoxSession session;
        private readonly UdpRtpTransport udpTransport;
        private readonly ControlChannel controlChannel;

        /// <summary>手机已连接（参数为手机端 TCP 地址）。</summary>
        public event Action<IPEndPoint> PhoneConnected;

        /// <summary>手机已断开（参数为断开前的手机端地址）。</summary>
        public event Action<IPEndPoint> PhoneDisconnected;

        /// <summary>手机已就绪，可开始推流。</summary>
        public event Action PhoneReady;

        /// <summary>收到手机按键（已注入本机后抛出，供 UI 展示反馈）。</summary>
        public event Action<KeyMessage> KeyReceived;

        /// <summary>会话状态变化。</summary>
        public event Action<PhoneBoxState, PhoneBoxState> StateChanged;

        public PhoneBoxServer()
            : this(PhoneBoxConfigStore.Current)
        {
        }

        public PhoneBoxServer(PhoneBoxConfig config)
        {
            this.config = config ?? new PhoneBoxConfig();
            this.session = new PhoneBoxSession();
            this.udpTransport = new UdpRtpTransport(this.config);
            this.controlChannel = new ControlChannel(this.config);

            WireEvents();
        }

        /// <summary>当前配置。</summary>
        public PhoneBoxConfig Config
        {
            get { return config; }
        }

        /// <summary>当前会话。</summary>
        public PhoneBoxSession Session
        {
            get { return session; }
        }

        /// <summary>启动服务。</summary>
        public void Start()
        {
            try
            {
                udpTransport.Start(config.UdpPort);
                controlChannel.Start();
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("启动手机盒子服务失败：" + ex);
                Stop();
                throw;
            }

            PhoneBoxLog.Info(
                "手机盒子服务已启动：TCP " + config.TcpPort + "，UDP " + config.UdpPort + "。");
        }

        /// <summary>停止服务。</summary>
        public void Stop()
        {
            controlChannel.Stop();
            udpTransport.Stop();
            session.MarkStopped();
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>把控制通道 / 会话事件桥接到本服务对外事件。</summary>
        private void WireEvents()
        {
            controlChannel.ClientConnected += OnClientConnected;
            controlChannel.ClientDisconnected += OnClientDisconnected;
            controlChannel.SessionReady += OnSessionReady;
            controlChannel.KeyReceived += OnKeyReceived;
            session.StateChanged += OnStateChanged;
        }

        private void OnClientConnected(IPEndPoint remote)
        {
            // 手机先通过 TCP 握手；UDP 目标地址约定为同一 IP + 电脑端 UDP 端口。
            var udpRemote = new IPEndPoint(remote.Address, config.UdpPort);
            udpTransport.SetRemote(udpRemote);

            // 支持重连：若上一次会话停留在 Stopped，先复位到 Idle 再进入 Connected。
            session.Reset();
            session.SetRemote(remote);

            PhoneConnected?.Invoke(remote);
        }

        private void OnClientDisconnected(IPEndPoint remote)
        {
            session.MarkStopped();
            PhoneDisconnected?.Invoke(remote);
        }

        private void OnSessionReady()
        {
            session.MarkReady();
            PhoneReady?.Invoke();
        }

        private void OnKeyReceived(KeyMessage message)
        {
            // 优先使用手机上报的扫描码；缺失时由虚拟键码换算扫描码。
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

            KeyReceived?.Invoke(message);
        }

        private void OnStateChanged(PhoneBoxState oldState, PhoneBoxState newState)
        {
            StateChanged?.Invoke(oldState, newState);
        }
    }
}
