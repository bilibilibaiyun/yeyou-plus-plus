using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using YeyouPlusPlus.PhoneBox;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 远程联机面板（主窗口内视图）：合并「主机（生成联机码 + 推流）」与「从机（输码接入）」两个功能。
    /// 从机配对成功后通过 <see cref="ClientTabRequested"/> 通知主窗口在新标签页显示画面。
    /// </summary>
    public partial class PhoneBoxPanel : UserControl
    {
        private readonly PhoneBoxConfig config;

        private RelayTunnel hostTunnel;
        private ControlChannel hostControl;
        private StreamingService hostStreaming;
        private bool hostStarted;
        private volatile bool hostStopping;

        /// <summary>从机配对成功，要求主窗口用该隧道新建画面标签页。</summary>
        public event Action<RelayTunnel> ClientTabRequested;

        /// <summary>抓屏目标窗口句柄提供器（由主窗口注入，取当前游戏标签页句柄）。</summary>
        public Func<IntPtr> CaptureWindowProvider { get; set; }

        public PhoneBoxPanel()
        {
            InitializeComponent();
            config = PhoneBoxConfigStore.Current;
        }

        // ================= 主机 =================

        private async void HostGenerateButton_Click(object sender, RoutedEventArgs e)
        {
            RelayTunnel tunnel = null;
            try
            {
                HostStatusText.Text = "正在连接中继服务器...";

                tunnel = new RelayTunnel();
                var code = await Task.Run(() => tunnel.SendHost());

                var control = new ControlChannel(config, tunnel);
                control.ClientConnected += OnHostClientConnected;
                control.ClientDisconnected += OnHostClientDisconnected;
                control.SessionReady += OnHostSessionReady;
                control.KeyReceived += OnHostKeyReceived;

                hostTunnel = tunnel;
                hostControl = control;
                // 隧道所有权已移交，后续异常不再重复释放。
                tunnel = null;

                HostCodeText.Text = code;
                control.Start();
                hostStarted = true;

                HostStatusText.Text = "联机码已生成，等待副设备连接...";
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("连接中继服务器失败：" + ex);
                HostStatusText.Text = "连接失败：" + ex.Message;
                if (tunnel != null)
                {
                    tunnel.Dispose();
                }
            }
            UpdateHostUi();
        }

        private void HostStopButton_Click(object sender, RoutedEventArgs e)
        {
            hostStopping = true;
            try
            {
                StopHostStreaming();
                var control = hostControl;
                hostControl = null;
                if (control != null)
                {
                    control.Dispose();
                }
                var tunnel = hostTunnel;
                hostTunnel = null;
                if (tunnel != null)
                {
                    tunnel.Dispose();
                }
                hostStarted = false;
                HostStatusText.Text = "已停止。";
            }
            finally
            {
                hostStopping = false;
            }
            UpdateHostUi();
        }

        private void HostCopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(HostCodeText.Text) || HostCodeText.Text.StartsWith("—"))
            {
                return;
            }
            try
            {
                Clipboard.SetText(HostCodeText.Text);
                HostStatusText.Text = "联机码已复制到剪贴板。";
            }
            catch (Exception ex)
            {
                HostStatusText.Text = "复制失败：" + ex.Message;
            }
        }

        private void OnHostClientConnected()
        {
            Dispatcher.BeginInvoke(new Action(() => HostStatusText.Text = "副设备已连接，等待就绪..."));
        }

        private void OnHostClientDisconnected()
        {
            if (hostStopping)
            {
                return;
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StopHostStreaming();
                hostStarted = false;
                // 释放本次会话的控制通道与隧道（幂等），供下次重新开房。
                var control = hostControl;
                hostControl = null;
                if (control != null)
                {
                    control.Dispose();
                }
                var tunnel = hostTunnel;
                hostTunnel = null;
                if (tunnel != null)
                {
                    tunnel.Dispose();
                }
                HostStatusText.Text = "副设备已断开。";
                UpdateHostUi();
            }));
        }

        private void OnHostSessionReady()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartHostStreaming();
                // 仅当推流真正启动后才提示「推流中」，避免覆盖 StartHostStreaming 失败时的错误提示。
                if (hostStreaming != null && hostStreaming.IsRunning)
                {
                    HostStatusText.Text = "副设备已就绪，推流中...";
                }
            }));
        }

        private void OnHostKeyReceived(KeyMessage message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                HostKeyText.Text = "按键：KeyCode=" + message.KeyCode
                    + " ScanCode=" + message.ScanCode + (message.IsDown ? " ↓" : " ↑")));
        }

        private void StartHostStreaming()
        {
            if (hostStreaming != null && hostStreaming.IsRunning)
            {
                return;
            }

            var tunnel = hostTunnel;
            if (tunnel == null)
            {
                return;
            }

            try
            {
                var encoder = new VideoEncoder();
                var streaming = new StreamingService(config, tunnel, encoder);
                if (CaptureWindowProvider != null)
                {
                    streaming.CaptureWindow = CaptureWindowProvider();
                }
                hostStreaming = streaming;
                streaming.Start();
            }
            catch (Exception ex)
            {
                // 编码器 Open 失败（如 openh264.dll 缺失）等不应导致进程崩溃，转成可恢复提示。
                PhoneBoxLog.Error("推流启动失败：" + ex);
                hostStopping = true;
                try
                {
                    StopHostStreaming();
                    var control = hostControl;
                    hostControl = null;
                    if (control != null)
                    {
                        control.Dispose();
                    }
                    var t = hostTunnel;
                    hostTunnel = null;
                    if (t != null)
                    {
                        t.Dispose();
                    }
                    hostStarted = false;
                    HostStatusText.Text = "推流启动失败：" + ex.Message;
                }
                finally
                {
                    hostStopping = false;
                }
                UpdateHostUi();
            }
        }

        private void StopHostStreaming()
        {
            if (hostStreaming != null)
            {
                hostStreaming.Stop();
                hostStreaming.Dispose();
                hostStreaming = null;
            }
        }

        private void UpdateHostUi()
        {
            HostGenerateButton.IsEnabled = !hostStarted;
            HostStopButton.IsEnabled = hostStarted || (hostStreaming != null && hostStreaming.IsRunning);
        }

        // ================= 从机 =================

        private async void ClientConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string code = ClientCodeBox.Text.Trim();
            if (code.Length != 8 || !code.All(char.IsDigit))
            {
                ClientStatusText.Text = "请输入 8 位数字联机码。";
                return;
            }

            ClientConnectButton.IsEnabled = false;
            ClientStatusText.Text = "正在连接中继服务器并配对...";
            RelayTunnel tunnel = null;
            try
            {
                tunnel = new RelayTunnel();
                await Task.Run(() => tunnel.Join(code));
                ClientStatusText.Text = "配对成功，正在打开画面标签页...";
                var handler = ClientTabRequested;
                if (handler == null)
                {
                    throw new InvalidOperationException("未注册从机标签页处理程序。");
                }
                handler(tunnel);
                // 隧道所有权已移交标签页，后续异常不再重复释放。
                tunnel = null;
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("从机连接失败：" + ex);
                ClientStatusText.Text = "连接失败：" + ex.Message;
                if (tunnel != null)
                {
                    tunnel.Dispose();
                }
            }
            finally
            {
                ClientConnectButton.IsEnabled = true;
            }
        }

        /// <summary>从机连接由主窗口接管后调用，复位从机区状态。</summary>
        public void OnClientTabOpened()
        {
            ClientStatusText.Text = "未连接";
        }

        /// <summary>退出时停止主机推流并释放资源（幂等）。</summary>
        public void Shutdown()
        {
            hostStopping = true;
            try
            {
                StopHostStreaming();
                var control = hostControl;
                hostControl = null;
                if (control != null)
                {
                    control.Dispose();
                }
                var tunnel = hostTunnel;
                hostTunnel = null;
                if (tunnel != null)
                {
                    tunnel.Dispose();
                }
                hostStarted = false;
            }
            finally
            {
                hostStopping = false;
            }
        }
    }
}
