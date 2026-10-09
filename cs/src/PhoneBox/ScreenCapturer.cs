using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 基于 GDI BitBlt 的屏幕捕获器。
    ///
    /// 关键点：CEF 使用 GPU 合成，PrintWindow 抓不到实际画面，因此这里直接
    /// 从屏幕 DC（CopyFromScreen 同款路径）用 BitBlt 抓取，验证能否拿到
    /// CEF 中 Flash 游戏的真实画面。
    /// </summary>
    public static class ScreenCapturer
    {
        private const uint SrcCopy = 0x00CC0020;
        private const uint CaptureBlt = 0x40000000;
        private const uint BiRgb = 0;
        private const uint DibRgbColors = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr hdcDest,
            int nXDest,
            int nYDest,
            int nWidth,
            int nHeight,
            IntPtr hdcSrc,
            int nXSrc,
            int nYSrc,
            uint dwRop);

        [DllImport("gdi32.dll")]
        private static extern bool GdiFlush();

        /// <summary>
        /// 抓取屏幕上指定矩形区域，返回独立的 24bpp RGB Bitmap。
        /// 坐标为物理像素（本进程按清单启用了 Per-Monitor DPI Aware）。
        /// </summary>
        public static Bitmap Capture(int x, int y, int width, int height)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "宽度必须大于 0。");
            }
            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), "高度必须大于 0。");
            }

            IntPtr screenDC = GetDC(IntPtr.Zero);
            if (screenDC == IntPtr.Zero)
            {
                throw new InvalidOperationException("获取屏幕 DC 失败（GetDC 返回 0）。");
            }

            IntPtr memDC = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldObject = IntPtr.Zero;

            try
            {
                memDC = CreateCompatibleDC(screenDC);
                if (memDC == IntPtr.Zero)
                {
                    throw new InvalidOperationException("创建兼容 DC 失败（CreateCompatibleDC 返回 0）。");
                }

                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = width;
                // 负高度 = top-down DIB，行序自上而下，便于后续按屏幕顺序读取。
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BiRgb;

                IntPtr bits;
                hBitmap = CreateDIBSection(screenDC, ref bmi, DibRgbColors, out bits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException("创建 DIB Section 失败（CreateDIBSection 返回 0）。");
                }

                oldObject = SelectObject(memDC, hBitmap);
                if (oldObject == IntPtr.Zero)
                {
                    throw new InvalidOperationException("把 DIB 选入 DC 失败（SelectObject 返回 0）。");
                }

                // CAPTUREBLT 确保分层窗口（layered window）也能被抓到。
                if (!BitBlt(memDC, 0, 0, width, height, screenDC, x, y, SrcCopy | CaptureBlt))
                {
                    throw new InvalidOperationException("BitBlt 抓屏失败。");
                }

                // 强制 GDI 批量命令落地，避免 GPU 合成内容尚未完成拷贝。
                GdiFlush();

                return CopyToOpaqueBitmap(hBitmap, width, height);
            }
            finally
            {
                if (oldObject != IntPtr.Zero)
                {
                    SelectObject(memDC, oldObject);
                }
                if (hBitmap != IntPtr.Zero)
                {
                    DeleteObject(hBitmap);
                }
                if (memDC != IntPtr.Zero)
                {
                    DeleteDC(memDC);
                }
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        /// <summary>
        /// 抓取指定窗口一帧并返回独立的 24bpp RGB Bitmap。
        /// 窗口句柄无效或取不到尺寸时，回退抓主显示器全屏。
        /// </summary>
        public static Bitmap Capture(IntPtr windowHandle)
        {
            if (windowHandle != IntPtr.Zero)
            {
                RECT rect;
                if (GetWindowRect(windowHandle, out rect) && rect.Width > 0 && rect.Height > 0)
                {
                    return Capture(rect.Left, rect.Top, rect.Width, rect.Height);
                }
            }

            var bounds = Screen.PrimaryScreen.Bounds;
            return Capture(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        /// <summary>
        /// 抓取主显示器全屏一帧并保存为 PNG（手机盒子 spike 验证用）。
        /// </summary>
        public static void CaptureToFile(string path)
        {
            var bounds = Screen.PrimaryScreen.Bounds;
            CaptureToFile(path, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        /// <summary>
        /// 抓取指定窗口一帧并保存为 PNG。抓取前先尝试把窗口置为前台，确保内容可见。
        /// </summary>
        public static void CaptureToFile(string path, IntPtr windowHandle)
        {
            if (windowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(windowHandle);
            }

            RECT rect;
            if (windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out rect)
                || rect.Width <= 0 || rect.Height <= 0)
            {
                var bounds = Screen.PrimaryScreen.Bounds;
                CaptureToFile(path, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                return;
            }

            CaptureToFile(path, rect.Left, rect.Top, rect.Width, rect.Height);
        }

        /// <summary>
        /// 抓取指定屏幕矩形并保存为 PNG。
        /// </summary>
        public static void CaptureToFile(string path, int x, int y, int width, int height)
        {
            var fullPath = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var bitmap = Capture(x, y, width, height))
            {
                bitmap.Save(fullPath, ImageFormat.Png);
            }
        }

        /// <summary>
        /// 把 GDI HBITMAP 复制成独立的 24bpp RGB Bitmap，丢弃 alpha 通道，
        /// 避免 BitBlt 抓屏时 alpha 为 0 导致 PNG 全透明。
        /// </summary>
        private static Bitmap CopyToOpaqueBitmap(IntPtr hBitmap, int width, int height)
        {
            using (var source = Image.FromHbitmap(hBitmap))
            {
                var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(result))
                {
                    g.DrawImage(source, 0, 0, width, height);
                }
                return result;
            }
        }
    }
}
