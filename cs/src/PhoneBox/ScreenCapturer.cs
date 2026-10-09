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

                // 直接从 DIB section 的像素指针读 RGB（跳过 alpha 字节）构造 24bpp 位图，
                // 避免 Image.FromHbitmap + DrawImage 在 alpha=0 时把画面画成黑色。
                return BuildOpaqueBitmap(bits, width, height);
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
        /// 把 32bpp BGRA DIB section 的像素数据直接转成 24bpp BGR Bitmap。
        /// 逐像素跳过 alpha 字节，彻底规避「alpha=0 导致画面被画成黑色」的问题。
        /// </summary>
        private static Bitmap BuildOpaqueBitmap(IntPtr bits, int width, int height)
        {
            int srcStride = width * 4; // 32bpp DIB，每行无填充
            var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int dstStride = data.Stride;
                var src = new byte[srcStride * height];
                var dst = new byte[dstStride * height];
                Marshal.Copy(bits, src, 0, src.Length);

                for (int y = 0; y < height; y++)
                {
                    int sr = y * srcStride;
                    int dr = y * dstStride;
                    for (int x = 0; x < width; x++)
                    {
                        int si = sr + x * 4;  // B, G, R, A
                        int di = dr + x * 3;  // B, G, R
                        dst[di] = src[si];
                        dst[di + 1] = src[si + 1];
                        dst[di + 2] = src[si + 2];
                    }
                }

                Marshal.Copy(dst, 0, data.Scan0, dst.Length);
            }
            finally
            {
                result.UnlockBits(data);
            }
            return result;
        }

        /// <summary>
        /// 抽样检测一帧是否「接近全黑」（用于诊断抓屏是否抓到了黑屏）。
        /// 每隔 10 像素抽样，若 99% 以上采样点为黑色（RGB&lt;16）则判定为疑似黑屏。
        /// </summary>
        public static bool IsLikelyBlack(Bitmap bmp)
        {
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0)
            {
                return true;
            }

            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                const int bpp = 3;
                var buffer = new byte[stride * bmp.Height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                int black = 0;
                int total = 0;
                const int step = 10;
                for (int y = 0; y < bmp.Height; y += step)
                {
                    int rowBase = y * stride;
                    for (int x = 0; x < bmp.Width; x += step)
                    {
                        int i = rowBase + x * bpp;
                        int b = buffer[i];
                        int g = buffer[i + 1];
                        int r = buffer[i + 2];
                        if (r < 16 && g < 16 && b < 16)
                        {
                            black++;
                        }
                        total++;
                    }
                }
                return total > 0 && black * 100 / total >= 99;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }
}
