using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace YeyouPlusPlus.PhoneBox.Client
{
    /// <summary>
    /// 解码后的一帧 I420 数据（保留 native 行跨距）。
    /// </summary>
    public sealed class YuvFrame
    {
        /// <summary>Y 平面（StrideY * Height 字节）。</summary>
        public byte[] Y;

        /// <summary>U 平面（StrideU * (Height/2) 字节）。</summary>
        public byte[] U;

        /// <summary>V 平面（StrideV * (Height/2) 字节）。</summary>
        public byte[] V;

        public int Width;
        public int Height;

        /// <summary>Y 平面行跨距（字节）。</summary>
        public int StrideY;

        /// <summary>U 平面行跨距（字节）。</summary>
        public int StrideU;

        /// <summary>V 平面行跨距（字节）。</summary>
        public int StrideV;
    }

    /// <summary>
    /// 基于 Cisco OpenH264 的软解码器（实现 ISVCDecoder）。
    ///
    /// 说明：
    ///   1. 直接 P/Invoke 调用 openh264.dll（与 <see cref="YeyouPlusPlus.PhoneBox.VideoEncoder"/> 同一套手法）。
    ///   2. 导出函数 WelsCreateDecoder / WelsDestroyDecoder 返回 C++ 对象指针，本类按其 vtable 手工解析函数指针。
    ///   3. vtable 槽位以 codec_api.h 中 ISVCDecoder 的虚函数声明顺序为准：
    ///      Initialize(0) / Uninitialize(1) / DecodeFrame(2) / DecodeFrameNoDelay(3) /
    ///      DecodeFrame2(4) / FlushFrame(5) / DecodeParser(6) / DecodeFrameEx(7) /
    ///      SetOption(8) / GetOption(9)。
    ///      注意：任务书给出的槽位顺序省略了 DecodeFrame / DecodeParser / DecodeFrameEx，
    ///      与真实 C++ vtable 不符；此处按 OpenH264 头文件校正，否则会错位导致崩溃。
    ///   4. 输入为 Annex-B access unit（每个 NAL 带 00 00 00 01 起始码），按起始码切分后
    ///      逐 NAL 喂 DecodeFrame2；SBufferInfo.iBufferStatus == 1 时输出就绪
    ///      （任务书写作 ==0 属笔误，OpenH264 头文件明确 0=未就绪、1=已就绪）。
    /// </summary>
    public sealed class H264Decoder : IDisposable
    {
        private const int VtblInitialize = 0;
        private const int VtblUninitialize = 1;
        private const int VtblDecodeFrame2 = 4;
        private const int VtblFlushFrame = 5;
        private const int VtblSetOption = 8;

        // EVideoBitstreamType
        private const int VideoBitstreamAvc = 0;

        // DECODER_OPTION
        private const int DecoderOptionTraceLevel = 9;

        // WELS_LOG
        private const int WelsLogQuiet = 0;

        private readonly object sync = new object();

        private IntPtr decoder = IntPtr.Zero;
        private IntPtr vtable = IntPtr.Zero;

        private InitializeDelegate initialize;
        private UninitializeDelegate uninitialize;
        private DecodeFrame2Delegate decodeFrame2;
        private SetOptionDelegate setOption;

        private IntPtr dstInfoPtr = IntPtr.Zero;
        private int dstInfoSize;

        private bool opened;

        /// <summary>是否已成功打开解码器。</summary>
        public bool IsOpened
        {
            get
            {
                lock (sync)
                {
                    return opened;
                }
            }
        }

        /// <summary>打开解码器（输出格式固定为 I420）。</summary>
        public void Open()
        {
            lock (sync)
            {
                if (opened)
                {
                    return;
                }

                try
                {
                    CreateDecoder();
                    InitializeDecoder();
                    AllocateBuffers();
                    opened = true;
                }
                catch
                {
                    ReleaseDecoder();
                    throw;
                }
            }

            PhoneBoxLog.Info("OpenH264 解码器已打开。");
        }

        /// <summary>解码一帧 Annex-B access unit，返回 I420 平面（无输出时返回 null）。</summary>
        public YuvFrame Decode(byte[] annexBAccessUnit)
        {
            if (annexBAccessUnit == null)
            {
                throw new ArgumentNullException(nameof(annexBAccessUnit));
            }

            lock (sync)
            {
                if (!opened || decoder == IntPtr.Zero)
                {
                    return null;
                }

                var nalus = SplitNalus(annexBAccessUnit);
                if (nalus.Count == 0)
                {
                    return null;
                }

                var pData = new IntPtr[3];
                var dataHandle = GCHandle.Alloc(pData, GCHandleType.Pinned);
                try
                {
                    IntPtr ppDst = dataHandle.AddrOfPinnedObject();

                    foreach (var nal in nalus)
                    {
                        FeedNal(nal, ppDst);
                        if (IsFrameReady())
                        {
                            break;
                        }
                    }

                    if (!IsFrameReady())
                    {
                        // 空输入再触发一次重建，兜底多切片 / 需要冲刷的情况。
                        FeedNal(null, ppDst);
                    }

                    if (!IsFrameReady())
                    {
                        return null;
                    }

                    return ExtractFrame(pData);
                }
                finally
                {
                    dataHandle.Free();
                }
            }
        }

        /// <summary>关闭解码器并释放 native 资源。</summary>
        public void Close()
        {
            lock (sync)
            {
                if (!opened && decoder == IntPtr.Zero)
                {
                    return;
                }
                opened = false;
                ReleaseDecoder();
            }
            PhoneBoxLog.Info("OpenH264 解码器已关闭。");
        }

        public void Dispose()
        {
            Close();
        }

        /// <summary>把单个 NAL（含起始码）喂给 DecodeFrame2；nal 为 null 表示空输入冲刷。</summary>
        private void FeedNal(byte[] nal, IntPtr ppDst)
        {
            GCHandle handle = default(GCHandle);
            IntPtr pSrc = IntPtr.Zero;
            int len = 0;
            try
            {
                if (nal != null && nal.Length > 0)
                {
                    handle = GCHandle.Alloc(nal, GCHandleType.Pinned);
                    pSrc = handle.AddrOfPinnedObject();
                    len = nal.Length;
                }

                int rv = decodeFrame2(decoder, pSrc, len, ppDst, dstInfoPtr);
                if (rv != 0 && rv != 0x10) // 0x10 = dsNoParamSets（首帧 SPS/PPS 未齐属正常）
                {
                    PhoneBoxLog.Warn("OpenH264 DecodeFrame2 返回 " + rv + "。");
                }
            }
            finally
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }

        private bool IsFrameReady()
        {
            return Marshal.ReadInt32(dstInfoPtr, 0) == 1;
        }

        /// <summary>从 native 输出缓冲取出 I420 三平面（保留行跨距）。</summary>
        private YuvFrame ExtractFrame(IntPtr[] pData)
        {
            var info = (SBufferInfo)Marshal.PtrToStructure(dstInfoPtr, typeof(SBufferInfo));

            int width = info.sSystemBuffer.iWidth;
            int height = info.sSystemBuffer.iHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            int strideY = info.sSystemBuffer.iStride[0];
            int strideUv = info.sSystemBuffer.iStride[1];
            if (strideY <= 0)
            {
                strideY = width;
            }
            if (strideUv <= 0)
            {
                strideUv = Math.Max(1, width / 2);
            }

            IntPtr yPtr = pData[0];
            IntPtr uPtr = pData[1];
            IntPtr vPtr = pData[2];
            if (yPtr == IntPtr.Zero || uPtr == IntPtr.Zero || vPtr == IntPtr.Zero)
            {
                return null;
            }

            int uvHeight = Math.Max(1, height / 2);

            var y = new byte[strideY * height];
            var u = new byte[strideUv * uvHeight];
            var v = new byte[strideUv * uvHeight];

            CopyPlane(yPtr, y, strideY, height);
            CopyPlane(uPtr, u, strideUv, uvHeight);
            CopyPlane(vPtr, v, strideUv, uvHeight);

            return new YuvFrame
            {
                Y = y,
                U = u,
                V = v,
                Width = width,
                Height = height,
                StrideY = strideY,
                StrideU = strideUv,
                StrideV = strideUv
            };
        }

        /// <summary>按行把 native 平面完整拷贝到托管数组（保留行跨距）。</summary>
        private static void CopyPlane(IntPtr src, byte[] dst, int stride, int height)
        {
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(IntPtr.Add(src, row * stride), dst, row * stride, stride);
            }
        }

        /// <summary>
        /// 把 Annex-B 字节流切分为带起始码的 NAL 列表。
        /// 正确识别 4 字节起始码 00 00 00 01 与 3 字节起始码 00 00 01。
        /// </summary>
        private static List<byte[]> SplitNalus(byte[] data)
        {
            var nalus = new List<byte[]>();
            int n = data.Length;
            if (n < 3)
            {
                return nalus;
            }

            var starts = new List<int>();
            int pos = 0;
            while (pos <= n - 3)
            {
                if (data[pos] == 0x00 && data[pos + 1] == 0x00 && data[pos + 2] == 0x01)
                {
                    int start = (pos > 0 && data[pos - 1] == 0x00) ? pos - 1 : pos;
                    starts.Add(start);
                    pos += 3;
                }
                else
                {
                    pos++;
                }
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = (i + 1 < starts.Count) ? starts[i + 1] : n;
                int len = end - start;
                if (len > 0)
                {
                    var nal = new byte[len];
                    Array.Copy(data, start, nal, 0, len);
                    nalus.Add(nal);
                }
            }

            return nalus;
        }

        private void CreateDecoder()
        {
            int rv = WelsCreateDecoder(ref decoder);
            if (rv != 0 || decoder == IntPtr.Zero)
            {
                throw new InvalidOperationException("WelsCreateDecoder 失败（返回 " + rv + "）。");
            }

            vtable = Marshal.ReadIntPtr(decoder);

            initialize = GetVtableDelegate<InitializeDelegate>(VtblInitialize);
            uninitialize = GetVtableDelegate<UninitializeDelegate>(VtblUninitialize);
            decodeFrame2 = GetVtableDelegate<DecodeFrame2Delegate>(VtblDecodeFrame2);
            setOption = GetVtableDelegate<SetOptionDelegate>(VtblSetOption);
        }

        private TDelegate GetVtableDelegate<TDelegate>(int slot) where TDelegate : class
        {
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            if (fn == IntPtr.Zero)
            {
                throw new InvalidOperationException("OpenH264 解码器 vtable 槽位 " + slot + " 为空。");
            }
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(fn);
        }

        private void InitializeDecoder()
        {
            int size = Marshal.SizeOf(typeof(SDecodingParam));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++)
                {
                    Marshal.WriteByte(p, i, 0);
                }

                var param = new SDecodingParam
                {
                    sVideoProperty = new SVideoProperty
                    {
                        size = (uint)Marshal.SizeOf(typeof(SVideoProperty)),
                        eVideoBsType = VideoBitstreamAvc
                    }
                };
                Marshal.StructureToPtr(param, p, false);

                int rv = initialize(decoder, p);
                if (rv != 0)
                {
                    throw new InvalidOperationException("OpenH264 解码器 Initialize 失败（返回 " + rv + "）。");
                }

                SetIntOption(DecoderOptionTraceLevel, WelsLogQuiet);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        private void SetIntOption(int option, int value)
        {
            IntPtr p = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(p, value);
                int rv = setOption(decoder, option, p);
                if (rv != 0)
                {
                    PhoneBoxLog.Warn("OpenH264 解码器 SetOption(" + option + ") 返回 " + rv + "。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        private void AllocateBuffers()
        {
            dstInfoSize = Marshal.SizeOf(typeof(SBufferInfo));
            dstInfoPtr = Marshal.AllocHGlobal(dstInfoSize);
            for (int i = 0; i < dstInfoSize; i++)
            {
                Marshal.WriteByte(dstInfoPtr, i, 0);
            }
        }

        private void ReleaseDecoder()
        {
            if (dstInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dstInfoPtr);
                dstInfoPtr = IntPtr.Zero;
            }

            if (decoder != IntPtr.Zero)
            {
                if (uninitialize != null)
                {
                    try
                    {
                        uninitialize(decoder);
                    }
                    catch (Exception ex)
                    {
                        PhoneBoxLog.Warn("OpenH264 解码器 Uninitialize 失败：" + ex.Message);
                    }
                }
                WelsDestroyDecoder(decoder);
                decoder = IntPtr.Zero;
            }

            vtable = IntPtr.Zero;
            initialize = null;
            uninitialize = null;
            decodeFrame2 = null;
            setOption = null;
        }

        // ---- P/Invoke ----

        [DllImport("openh264", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WelsCreateDecoder(ref IntPtr ppDecoder);

        [DllImport("openh264", CallingConvention = CallingConvention.Cdecl)]
        private static extern void WelsDestroyDecoder(IntPtr pDecoder);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeDelegate(IntPtr self, IntPtr pParam);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UninitializeDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecodeFrame2Delegate(IntPtr self, IntPtr pSrc, int iSrcLen, IntPtr ppDst, IntPtr pDstInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetOptionDelegate(IntPtr self, int eOptionId, IntPtr pOption);

        // ---- OpenH264 结构体（布局匹配 codec_def.h，见类注释） ----

        [StructLayout(LayoutKind.Sequential)]
        private struct SVideoProperty
        {
            public uint size;
            public int eVideoBsType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SDecodingParam
        {
            public IntPtr pFileNameRestructed;
            public uint uiCpuLoad;
            public byte uiTargetDqLayer;
            public int eEcActiveIdc;
            [MarshalAs(UnmanagedType.I1)] public bool bParseOnly;
            public SVideoProperty sVideoProperty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SSystemBuffer
        {
            public int iWidth;
            public int iHeight;
            public int iFormat;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public int[] iStride;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SBufferInfo
        {
            public int iBufferStatus;
            public ulong uiInBsTimeStamp;
            public ulong uiOutYuvTimeStamp;
            public SSystemBuffer sSystemBuffer;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public IntPtr[] pDst;
        }
    }
}
