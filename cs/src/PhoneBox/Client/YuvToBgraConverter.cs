using System;

namespace YeyouPlusPlus.PhoneBox.Client
{
    /// <summary>
    /// I420 → BGRA 颜色空间转换（BT.601 全范围整数近似）。
    ///
    /// 与 <see cref="YeyouPlusPlus.PhoneBox.VideoEncoder"/> 的 RGB→I420 系数互为逆变换，
    /// 保证从机端还原出的颜色与主机端一致。支持按行跨距（stride）读取平面。
    /// </summary>
    public static class YuvToBgraConverter
    {
        /// <summary>把带行跨距的 I420 平面转换为 BGRA 字节数组（length = Width*Height*4）。</summary>
        public static byte[] Convert(YuvFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }
            if (frame.Y == null || frame.U == null || frame.V == null)
            {
                throw new ArgumentException("YuvFrame 平面数据不能为 null。");
            }
            if (frame.Width <= 0 || frame.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frame), "分辨率必须大于 0。");
            }

            int w = frame.Width;
            int h = frame.Height;
            int strideY = frame.StrideY > 0 ? frame.StrideY : w;
            int strideU = frame.StrideU > 0 ? frame.StrideU : Math.Max(1, w / 2);
            int strideV = frame.StrideV > 0 ? frame.StrideV : Math.Max(1, w / 2);

            var bgra = new byte[w * h * 4];

            unsafe
            {
                fixed (byte* pY = frame.Y, pU = frame.U, pV = frame.V, pOut = bgra)
                {
                    for (int row = 0; row < h; row++)
                    {
                        byte* yRow = pY + row * strideY;
                        byte* uRow = pU + (row >> 1) * strideU;
                        byte* vRow = pV + (row >> 1) * strideV;
                        byte* outRow = pOut + row * w * 4;

                        for (int col = 0; col < w; col++)
                        {
                            int yy = yRow[col];
                            int uu = uRow[col >> 1] - 128;
                            int vv = vRow[col >> 1] - 128;

                            // BT.601 全范围整数近似（系数 ×256）。
                            int r = yy + ((359 * vv + 128) >> 8);
                            int g = yy - ((88 * uu + 183 * vv + 128) >> 8);
                            int b = yy + ((454 * uu + 128) >> 8);

                            int o = col * 4;
                            outRow[o] = ClampByte(b);
                            outRow[o + 1] = ClampByte(g);
                            outRow[o + 2] = ClampByte(r);
                            outRow[o + 3] = 255;
                        }
                    }
                }
            }

            return bgra;
        }

        private static byte ClampByte(int v)
        {
            if (v < 0)
            {
                return 0;
            }
            if (v > 255)
            {
                return 255;
            }
            return (byte)v;
        }
    }
}
