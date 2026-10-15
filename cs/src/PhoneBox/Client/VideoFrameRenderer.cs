using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YeyouPlusPlus.PhoneBox.Client
{
    /// <summary>
    /// 把解码出的 YuvFrame 写入 WriteableBitmap 上屏（latest-frame-wins）。
    ///
    /// Present() 可在任意线程调用；内部通过目标 Image 的 Dispatcher 回 UI 线程，
    /// 只保留最新一帧，避免解码线程快于渲染线程时队列堆积导致延迟。
    /// 视频尺寸变化时自动重建 WriteableBitmap。
    /// </summary>
    public sealed class VideoFrameRenderer : IDisposable
    {
        private readonly Image target;
        private readonly Dispatcher dispatcher;
        private readonly object sync = new object();

        private WriteableBitmap bitmap;
        private int bitmapWidth;
        private int bitmapHeight;

        private YuvFrame pending;
        private bool renderScheduled;
        private bool disposed;

        public VideoFrameRenderer(Image target)
        {
            this.target = target ?? throw new ArgumentNullException(nameof(target));
            this.dispatcher = target.Dispatcher;
        }

        /// <summary>提交一帧 I420 数据。非 UI 线程安全。</summary>
        public void Present(YuvFrame frame)
        {
            if (frame == null)
            {
                return;
            }

            bool schedule;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                pending = frame;
                schedule = !renderScheduled;
                if (schedule)
                {
                    renderScheduled = true;
                }
            }

            if (schedule)
            {
                dispatcher.BeginInvoke(new Action(RenderFrame));
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                pending = null;
                renderScheduled = false;
            }
        }

        private void RenderFrame()
        {
            YuvFrame frame;
            lock (sync)
            {
                renderScheduled = false;
                if (disposed)
                {
                    return;
                }

                frame = pending;
                pending = null;
            }

            if (frame == null)
            {
                return;
            }

            byte[] bgra = YuvToBgraConverter.Convert(frame);
            EnsureBitmap(frame.Width, frame.Height);
            if (bitmap == null)
            {
                return;
            }

            bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                bgra,
                frame.Width * 4,
                0);
        }

        private void EnsureBitmap(int width, int height)
        {
            if (bitmap != null && bitmapWidth == width && bitmapHeight == height)
            {
                return;
            }

            bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bitmapWidth = width;
            bitmapHeight = height;
            target.Source = bitmap;
        }
    }
}
