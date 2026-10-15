using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 视频编码器抽象接口。
    ///
    /// 本阶段仅定义契约，后续用 FFmpeg（libx264 / NVENC 等）实现同一接口并注入
    /// <see cref="StreamingService"/>，即可完成真正的 H.264 编码，无需改动框架。
    /// </summary>
    public interface IVideoEncoder
    {
        /// <summary>按目标参数打开编码器。</summary>
        void Open(int width, int height, int fps, int bitrate);

        /// <summary>编码一帧，返回 H.264 Annex-B 字节流（可能为 null，表示本帧未产出）。</summary>
        byte[] Encode(Bitmap frame);

        /// <summary>关闭编码器并释放资源。</summary>
        void Close();
    }

    /// <summary>
    /// 推流编排框架：串联「捕获 → 编码 → 中继隧道发送」循环。
    ///
    ///   - 捕获线程按目标帧率抓屏，写入单缓冲；
    ///   - 编码线程只取最新一帧（最新帧丢弃策略，避免队列堆积导致延迟）；
    ///   - 编码出 H.264 Annex-B 完整帧后直接经 RelayTunnel 以 type=0x02 帧发送
    ///     （不再做 RTP 分包；UdpRtpTransport / RtpPacketizer 已不参与新链路）。
    /// </summary>
    public sealed class StreamingService : IDisposable
    {
        private readonly PhoneBoxConfig config;
        private readonly RelayTunnel tunnel;

        private readonly object frameLock = new object();
        private Bitmap latestFrame;
        private IVideoEncoder encoder;

        private volatile bool running;
        private Thread captureThread;
        private Thread encodeThread;

        public StreamingService(PhoneBoxConfig config, RelayTunnel tunnel, IVideoEncoder encoder)
        {
            this.config = config ?? new PhoneBoxConfig();
            this.tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
            this.encoder = encoder;
        }

        /// <summary>当前编码器。</summary>
        public IVideoEncoder Encoder
        {
            get { return encoder; }
            set { encoder = value; }
        }

        /// <summary>是否正在推流。</summary>
        public bool IsRunning
        {
            get { return running; }
        }

        /// <summary>
        /// 捕获目标窗口句柄（页游主窗口或当前游戏标签页的 HwndHost 句柄）。
        /// 为 0 时回退抓主显示器全屏。
        /// </summary>
        public IntPtr CaptureWindow { get; set; }

        /// <summary>启动捕获线程与编码线程。</summary>
        public void Start()
        {
            if (running)
            {
                return;
            }

            if (encoder != null)
            {
                encoder.Open(config.Width, config.Height, config.FrameRate, config.BitrateKbps);
            }
            else
            {
                PhoneBoxLog.Warn("StreamingService 未注入 IVideoEncoder，推流循环仅捕获不编码。");
            }

            running = true;
            captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "PhoneBoxCapture"
            };
            encodeThread = new Thread(EncodeLoop)
            {
                IsBackground = true,
                Name = "PhoneBoxEncode"
            };

            captureThread.Start();
            encodeThread.Start();

            PhoneBoxLog.Info("StreamingService 已启动，目标 " + config.FrameRate + " fps。");
        }

        /// <summary>停止推流循环并释放资源。</summary>
        public void Stop()
        {
            if (!running)
            {
                return;
            }

            running = false;

            if (captureThread != null)
            {
                captureThread.Join(2000);
                captureThread = null;
            }
            if (encodeThread != null)
            {
                encodeThread.Join(2000);
                encodeThread = null;
            }

            lock (frameLock)
            {
                if (latestFrame != null)
                {
                    latestFrame.Dispose();
                    latestFrame = null;
                }
            }

            if (encoder != null)
            {
                try
                {
                    encoder.Close();
                }
                catch (Exception ex)
                {
                    PhoneBoxLog.Warn("关闭编码器失败：" + ex.Message);
                }
            }

            PhoneBoxLog.Info("StreamingService 已停止。");
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// 捕获线程：按帧率抓屏，写单缓冲。新帧到达时直接丢弃缓冲中尚未被编码的旧帧。
        /// 捕获目标优先为 CaptureWindow（游戏窗口），未设置时抓主显示器全屏。
        /// </summary>
        private void CaptureLoop()
        {
            long frameCount = 0;
            while (running)
            {
                try
                {
                    var frame = CaptureWindow != IntPtr.Zero
                        ? ScreenCapturer.Capture(CaptureWindow)
                        : ScreenCapturer.Capture(
                            Screen.PrimaryScreen.Bounds.X,
                            Screen.PrimaryScreen.Bounds.Y,
                            Screen.PrimaryScreen.Bounds.Width,
                            Screen.PrimaryScreen.Bounds.Height);

                    frameCount++;
                    if (frameCount == 1 || frameCount % 60 == 0)
                    {
                        bool black = ScreenCapturer.IsLikelyBlack(frame);
                        PhoneBoxLog.Info(
                            "抓屏诊断：第 " + frameCount + " 帧，尺寸 " + frame.Width + "x" + frame.Height
                            + (black ? "，【疑似全黑！】" : "，画面正常"));
                    }

                    lock (frameLock)
                    {
                        var old = latestFrame;
                        latestFrame = frame;
                        if (old != null)
                        {
                            old.Dispose(); // 最新帧丢弃：未编码的旧帧直接释放
                        }
                    }
                }
                catch (Exception ex)
                {
                    PhoneBoxLog.Warn("抓屏失败：" + ex.Message);
                }

                Thread.Sleep(1000 / Math.Max(1, config.FrameRate));
            }
        }

        /// <summary>
        /// 编码线程：只取最新一帧编码，并直接作为 type=0x02 帧经中继隧道发送。
        /// 取走帧后缓冲置空，捕获线程再写入新帧即完成“最新帧丢弃”。
        /// </summary>
        private void EncodeLoop()
        {
            long encodeCount = 0;
            while (running)
            {
                Bitmap frame = null;
                lock (frameLock)
                {
                    if (latestFrame != null)
                    {
                        frame = latestFrame;
                        latestFrame = null;
                    }
                }

                if (frame == null)
                {
                    Thread.Sleep(5);
                    continue;
                }

                try
                {
                    if (encoder == null)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    byte[] encoded = encoder.Encode(frame);
                    if (encoded == null || encoded.Length == 0)
                    {
                        continue;
                    }

                    encodeCount++;
                    if (encodeCount == 1 || encodeCount % 60 == 0)
                    {
                        PhoneBoxLog.Info("编码诊断：第 " + encodeCount + " 帧，H.264 " + encoded.Length + " 字节");
                    }

                    tunnel.WriteFrame(RelayTunnel.FrameTypeVideo, encoded);
                }
                catch (Exception ex)
                {
                    PhoneBoxLog.Warn("推流循环异常：" + ex.Message);
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
    }
}
