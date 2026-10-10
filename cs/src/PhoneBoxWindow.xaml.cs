using System;
using System.ComponentModel;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using YeyouPlusPlus.PhoneBox;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 远程联机窗口：生成联机码、启动 / 停止 PhoneBox 服务与推流，
    /// 并把「手机已连接 / 手机就绪 / 收到按键」等状态展示给用户。
    /// </summary>
    public partial class PhoneBoxWindow : Window
    {
        private readonly JoinCodeGenerator joinCode = new JoinCodeGenerator();
        private readonly PhoneBoxServer server;
        private readonly IntPtr captureWindow;
        private readonly EasyTierManager easyTier = new EasyTierManager();

        private StreamingService streaming;
        private bool serverStarted;

        /// <summary>是否正在执行真正的关闭（Shutdown 路径）；否则点 X / 关闭按钮仅隐藏窗口。</summary>
        private bool _shuttingDown;

        public PhoneBoxWindow(IntPtr captureWindow)
        {
            InitializeComponent();

            this.captureWindow = captureWindow;

            server = new PhoneBoxServer();
            server.TokenValidator = joinCode.VerifyToken;
            server.PhoneConnected += OnPhoneConnected;
            server.PhoneDisconnected += OnPhoneDisconnected;
            server.PhoneReady += OnPhoneReady;
            server.KeyReceived += OnKeyReceived;

            UpdateUiState();
        }

        private void GenerateCodeButton_Click(object sender, RoutedEventArgs e)
        {
            string code = joinCode.GenerateJoinCode();
            JoinCodeText.Text = code;
            AddressText.Text = joinCode.CurrentIp + ":" + joinCode.CurrentPort;
            UpdateStatus("联机码已生成，等待手机连接。");
        }

        /// <summary>开启跨网络联机：启动 EasyTier 组网（需管理员权限），生成跨网络联机码。</summary>
        private async void RemoteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!easyTier.IsAvailable)
            {
                UpdateStatus("跨网络组件缺失，无法使用。");
                return;
            }

            try
            {
                UpdateStatus("正在启动跨网络组网（首次会弹出管理员授权）...");
                JoinCodeGenerator.GenerateNetworkCredentials(out string name, out string secret);
                await Task.Run(() => easyTier.Start(name, secret));

                UpdateStatus("正在获取虚拟网络地址...");
                string ip = await Task.Run(() => easyTier.QueryVirtualIp(25));
                if (string.IsNullOrEmpty(ip))
                {
                    UpdateStatus("跨网络组网失败：未能获取虚拟 IP。请确认已允许管理员授权，且网络能连通公共节点。");
                    return;
                }

                string code = joinCode.GenerateRemoteJoinCode(ip, name, secret);
                JoinCodeText.Text = code;
                AddressText.Text = "虚拟 " + ip + ":" + joinCode.CurrentPort;
                UpdateStatus("跨网络联机码已生成，手机在外网也可连接。");
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("跨网络组网失败：" + ex);
                UpdateStatus("跨网络组网失败：" + ex.Message);
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                server.Start();
                serverStarted = true;
                UpdateStatus("服务已启动，等待手机连接...");
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("启动远程联机服务失败：" + ex);
                UpdateStatus("启动失败：" + ex.Message);
            }
            UpdateUiState();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopStreaming();
            server.Stop();
            easyTier.Stop();
            serverStarted = false;
            UpdateStatus("已停止。");
            UpdateUiState();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(JoinCodeText.Text))
            {
                return;
            }
            try
            {
                Clipboard.SetText(JoinCodeText.Text);
                UpdateStatus("联机码已复制到剪贴板。");
            }
            catch (Exception ex)
            {
                UpdateStatus("复制失败：" + ex.Message);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 只隐藏窗口，推流继续；真正的清理在 Shutdown() 中完成。
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 点 X / 关闭按钮只是隐藏窗口（推流不中断）；只有 Shutdown() 才真正关闭。
            if (!_shuttingDown)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }

        /// <summary>
        /// 真正关闭窗口并释放推流 / 服务资源（供主窗口退出时调用）。
        /// </summary>
        public void Shutdown()
        {
            _shuttingDown = true;
            StopStreaming();
            easyTier.Stop();
            server.Dispose();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopStreaming();
            server.Dispose();
            base.OnClosed(e);
        }

        private void OnPhoneConnected(IPEndPoint remote)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                UpdateStatus("手机已连接：" + remote + "，等待就绪...")));
        }

        private void OnPhoneDisconnected(IPEndPoint remote)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopStreaming();
                UpdateStatus("手机已断开。");
                UpdateUiState();
            }));
        }

        private void OnPhoneReady()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartStreaming();
                UpdateStatus("手机已就绪，推流中...");
                UpdateUiState();
            }));
        }

        private void OnKeyReceived(KeyMessage message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                KeyInfoText.Text = "按键：KeyCode=" + message.KeyCode
                    + " ScanCode=" + message.ScanCode + (message.IsDown ? " ↓" : " ↑")));
        }

        private void StartStreaming()
        {
            if (streaming != null && streaming.IsRunning)
            {
                return;
            }

            var config = server.Config;
            var encoder = new VideoEncoder();
            streaming = new StreamingService(config, server.UdpTransport, encoder)
            {
                CaptureWindow = captureWindow
            };
            streaming.Start();
            server.Session.MarkStreaming();
        }

        private void StopStreaming()
        {
            if (streaming != null)
            {
                streaming.Stop();
                streaming.Dispose();
                streaming = null;
            }
        }

        private void UpdateStatus(string text)
        {
            StatusText.Text = text;
            PhoneBoxLog.Info("远程联机状态：" + text);
        }

        private void UpdateUiState()
        {
            GenerateCodeButton.IsEnabled = !serverStarted;
            StartButton.IsEnabled = !serverStarted;
            StopButton.IsEnabled = serverStarted || (streaming != null && streaming.IsRunning);
        }
    }
}
