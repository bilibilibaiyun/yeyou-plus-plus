using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using YeyouPlusPlus.PhoneBox;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 远程联机窗口（主机）：连接中继服务器获取 8 位联机码，等待副设备配对，
    /// 配对成功后自动开始推流，并把「副设备已连接 / 已就绪 / 收到按键」等状态展示给用户。
    /// </summary>
    public partial class PhoneBoxWindow : Window
    {
        private readonly IntPtr captureWindow;
        private readonly PhoneBoxConfig config;
        private readonly RelayTunnel relayTunnel = new RelayTunnel();
        private readonly ControlChannel controlChannel;

        private StreamingService streaming;
        private bool started;

        /// <summary>用户主动停止时为 true，用于抑制「副设备已断开」的提示。</summary>
        private volatile bool stopping;

        /// <summary>是否正在执行真正的关闭（Shutdown 路径）；否则点 X / 关闭按钮仅隐藏窗口。</summary>
        private bool _shuttingDown;

        public PhoneBoxWindow(IntPtr captureWindow)
        {
            InitializeComponent();

            this.captureWindow = captureWindow;
            this.config = PhoneBoxConfigStore.Current;
            this.controlChannel = new ControlChannel(config, relayTunnel);

            controlChannel.ClientConnected += OnClientConnected;
            controlChannel.ClientDisconnected += OnClientDisconnected;
            controlChannel.SessionReady += OnSessionReady;
            controlChannel.KeyReceived += OnKeyReceived;

            UpdateUiState();
        }

        /// <summary>生成联机码：连接中继服务器并登记为主机，拿到 8 位码后等待副设备配对。</summary>
        private async void RemoteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateStatus("正在连接中继服务器...");
                string code = await Task.Run(() => relayTunnel.SendHost());

                JoinCodeText.Text = code;
                AddressText.Text = "中继服务器 " + relayTunnel.ServerHost + ":" + relayTunnel.ServerPort;
                controlChannel.Start();
                started = true;

                UpdateStatus("联机码已生成，等待副设备连接...");
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("连接中继服务器失败：" + ex);
                UpdateStatus("连接中继服务器失败：" + ex.Message);
            }
            UpdateUiState();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            stopping = true;
            try
            {
                StopStreaming();
                controlChannel.Stop();
                started = false;
                UpdateStatus("已停止。");
            }
            finally
            {
                stopping = false;
            }
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
        /// 真正关闭窗口并释放推流 / 隧道资源（供主窗口退出时调用）。
        /// </summary>
        public void Shutdown()
        {
            _shuttingDown = true;
            stopping = true;
            try
            {
                StopStreaming();
                controlChannel.Dispose();
                relayTunnel.Dispose();
            }
            finally
            {
                stopping = false;
            }
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopStreaming();
            controlChannel.Dispose();
            relayTunnel.Dispose();
            base.OnClosed(e);
        }

        private void OnClientConnected()
        {
            Dispatcher.BeginInvoke(new Action(() =>
                UpdateStatus("副设备已连接，等待就绪...")));
        }

        private void OnClientDisconnected()
        {
            if (stopping)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopStreaming();
                started = false;
                UpdateStatus("副设备已断开。");
                UpdateUiState();
            }));
        }

        private void OnSessionReady()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartStreaming();
                UpdateStatus("副设备已就绪，推流中...");
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

            var encoder = new VideoEncoder();
            streaming = new StreamingService(config, relayTunnel, encoder)
            {
                CaptureWindow = captureWindow
            };
            streaming.Start();
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
            RemoteButton.IsEnabled = !started;
            StopButton.IsEnabled = started || (streaming != null && streaming.IsRunning);
        }
    }
}
