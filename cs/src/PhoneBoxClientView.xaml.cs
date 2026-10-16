using System;
using System.Windows;
using System.Windows.Controls;
using YeyouPlusPlus.PhoneBox;
using YeyouPlusPlus.PhoneBox.Client;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 电脑连电脑「从机」画面标签内容：接收主机的推流画面并展示虚拟按键，
    /// 用键盘 / 鼠标操作把按键发回主机。由主窗口以标签页形式承载。
    /// </summary>
    public partial class PhoneBoxClientView : UserControl, IDisposable
    {
        private readonly VideoFrameRenderer renderer;
        private readonly VirtualKeyOverlay keyOverlay = new VirtualKeyOverlay();

        private ControlChannelClient controlClient;
        private H264Decoder decoder;
        private bool connected;

        /// <summary>用户主动断开时为 true，用于抑制「连接已断开」的误提示。</summary>
        private volatile bool stopping;

        /// <summary>连接状态变化（供主窗口更新标签标题等）。</summary>
        public event Action<bool> ConnectedChanged;

        public bool IsConnected
        {
            get { return connected; }
        }

        public PhoneBoxClientView()
        {
            InitializeComponent();

            renderer = new VideoFrameRenderer(VideoImage);
            VideoFrame.Children.Add(keyOverlay);
            keyOverlay.KeyAction += OnKeyAction;
        }

        /// <summary>接收已配对的隧道并启动从机会话（发 Hello → 收 Config → 发 Ready → 收视频）。</summary>
        public void Start(RelayTunnel tunnel)
        {
            if (connected)
            {
                return;
            }

            var ctrl = new ControlChannelClient(tunnel);
            ctrl.ConfigReceived += OnConfigReceived;
            ctrl.Error += OnError;
            ctrl.Disconnected += OnDisconnected;
            ctrl.VideoFrameReceived += OnVideoFrameReceived;
            controlClient = ctrl;
            ctrl.Connect();

            connected = true;
            ConnectedChanged?.Invoke(true);
        }

        /// <summary>把键盘捕获挂到宿主窗口（从机标签激活时调用）。</summary>
        public void AttachKeyboard(Window window)
        {
            keyOverlay.AttachKeyboard(window);
        }

        /// <summary>解除键盘捕获（从机标签失活 / 关闭时调用）。</summary>
        public void DetachKeyboard(Window window)
        {
            keyOverlay.DetachKeyboard(window);
            keyOverlay.ReleaseAll();
        }

        private void OnConfigReceived(ConfigMessage config)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                int width = Math.Max(2, (config.Width / 2) * 2);
                int height = Math.Max(2, (config.Height / 2) * 2);
                VideoFrame.Width = width;
                VideoFrame.Height = height;
                // 电脑端从机不做虚拟按键映射，只透传真实键盘按键（见 VirtualKeyOverlay.MapPhysicalKey）。
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
            PhoneBoxLog.Warn("从机错误：" + message);
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
                ConnectedChanged?.Invoke(false);
            }));
        }

        /// <summary>主动断开会话。</summary>
        public void Disconnect()
        {
            stopping = true;
            try
            {
                Cleanup();
                connected = false;
            }
            finally
            {
                stopping = false;
            }
            ConnectedChanged?.Invoke(false);
        }

        /// <summary>清理本次会话资源（幂等）。</summary>
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
        }

        public void Dispose()
        {
            Disconnect();
            renderer.Dispose();
        }
    }
}
