using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using YeyouPlusPlus.PhoneBox;
using YeyouPlusPlus.PhoneBox.Client;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 电脑连电脑「从机」窗口：输入 8 位联机码连接主机，接收推流画面并展示虚拟按键，
    /// 用键盘 / 鼠标操作把按键发回主机。
    /// </summary>
    public partial class PhoneBoxClientWindow : Window
    {
        private readonly RelayTunnel relayTunnel = new RelayTunnel();
        private readonly VideoFrameRenderer renderer;
        private readonly VirtualKeyOverlay keyOverlay = new VirtualKeyOverlay();

        private ControlChannelClient controlClient;
        private H264Decoder decoder;
        private bool connected;

        /// <summary>用户主动断开时为 true，用于抑制「连接已断开」的误提示。</summary>
        private volatile bool stopping;

        public PhoneBoxClientWindow()
        {
            InitializeComponent();

            renderer = new VideoFrameRenderer(VideoImage);
            VideoFrame.Children.Add(keyOverlay);
            keyOverlay.AttachKeyboard(this);
            keyOverlay.KeyAction += OnKeyAction;

            UpdateUiState();
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (connected)
            {
                DisconnectSession();
                return;
            }

            string code = JoinCodeBox.Text.Trim();
            if (code.Length != 8 || !code.All(char.IsDigit))
            {
                StatusText.Text = "请输入 8 位数字联机码。";
                return;
            }

            ActionButton.IsEnabled = false;
            StatusText.Text = "正在连接中继服务器并配对...";
            try
            {
                await Task.Run(() => relayTunnel.Join(code));
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("从机连接失败：" + ex);
                StatusText.Text = "连接失败：" + ex.Message;
                ActionButton.IsEnabled = true;
                return;
            }

            // 配对成功：创建控制通道客户端，发送 Hello 并启动统一读线程（控制 + 视频）。
            var ctrl = new ControlChannelClient(relayTunnel);
            ctrl.ConfigReceived += OnConfigReceived;
            ctrl.Error += OnError;
            ctrl.Disconnected += OnDisconnected;
            ctrl.VideoFrameReceived += OnVideoFrameReceived;
            controlClient = ctrl;
            ctrl.Connect();

            connected = true;
            StatusText.Text = "已配对，等待主机配置...";
            UpdateUiState();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 从机窗口关闭即断开连接（键盘 / 画面依赖本窗口，不保留后台会话）。
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            stopping = true;
            try
            {
                Cleanup();
            }
            finally
            {
                stopping = false;
            }

            keyOverlay.DetachKeyboard(this);
            keyOverlay.ReleaseAll();
            renderer.Dispose();
            relayTunnel.Dispose();
            base.OnClosed(e);
        }

        private void OnConfigReceived(ConfigMessage config)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                int width = Math.Max(2, (config.Width / 2) * 2);
                int height = Math.Max(2, (config.Height / 2) * 2);
                VideoFrame.Width = width;
                VideoFrame.Height = height;

                var keys = ClientKeyLayout.Parse(config.KeyLayout) ?? ClientKeyLayout.Default();
                keyOverlay.SetKeys(keys);
                StatusText.Text = "已连接，推流中...";
            }));

            // 打开解码器后再发 Ready，确保主机开始推流时解码器已就绪。
            try
            {
                var d = new H264Decoder();
                d.Open();
                decoder = d;

                var ctrl = controlClient;
                if (ctrl != null)
                {
                    ctrl.SendReady();
                }
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Error("从机启动解码失败：" + ex);
                RaiseError("启动解码失败：" + ex.Message);
            }
        }

        private void OnVideoFrameReceived(byte[] annexB)
        {
            var d = decoder;
            if (d == null)
            {
                return;
            }

            try
            {
                var frame = d.Decode(annexB);
                if (frame != null)
                {
                    renderer.Present(frame);
                }
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Warn("从机解码失败：" + ex.Message);
            }
        }

        private void OnKeyAction(int keyCode, int scanCode, bool isDown)
        {
            var ctrl = controlClient;
            if (ctrl != null && ctrl.IsRunning)
            {
                ctrl.SendKey(keyCode, scanCode, isDown);
            }
        }

        private void OnError(string message)
        {
            RaiseError(message);
        }

        private void OnDisconnected()
        {
            if (stopping)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Cleanup();
                keyOverlay.ReleaseAll();
                connected = false;
                StatusText.Text = "连接已断开。";
                UpdateUiState();
            }));
        }

        private void DisconnectSession()
        {
            stopping = true;
            try
            {
                Cleanup();
                connected = false;
                StatusText.Text = "已断开。";
            }
            finally
            {
                stopping = false;
            }
            UpdateUiState();
        }

        /// <summary>清理本次会话资源（幂等）：控制通道、解码器、隧道。</summary>
        private void Cleanup()
        {
            var ctrl = controlClient;
            controlClient = null;

            var dec = decoder;
            decoder = null;

            if (ctrl != null)
            {
                ctrl.ConfigReceived -= OnConfigReceived;
                ctrl.Error -= OnError;
                ctrl.Disconnected -= OnDisconnected;
                ctrl.VideoFrameReceived -= OnVideoFrameReceived;
                ctrl.Disconnect();
                ctrl.Dispose();
            }

            if (dec != null)
            {
                dec.Dispose();
            }

            // 只 Close 不 Dispose，允许下次重新 Join 复用同一隧道对象。
            relayTunnel.Close();
        }

        private void RaiseError(string message)
        {
            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "错误：" + message));
        }

        private void UpdateUiState()
        {
            ActionButton.Content = connected ? "断开" : "连接";
            ActionButton.IsEnabled = true;
            JoinCodeBox.IsEnabled = !connected;
        }
    }
}
