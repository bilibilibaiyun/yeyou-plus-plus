using System;
using System.Net;
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

        private StreamingService streaming;
        private bool serverStarted;

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
