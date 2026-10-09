using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 基于 Cisco OpenH264 的软编码器（实现 <see cref="IVideoEncoder"/>）。
    ///
    /// 说明：
    ///   1. 直接 P/Invoke 调用 openh264.dll（BSD 许可，无 GPU 驱动依赖）。
    ///   2. OpenH264 是 C++ 抽象接口（ISVCEncoder），本类按其官方 C 头文件
    ///      （codec/api/wels/codec_api.h 中 ISVCEncoderVtbl 的布局）手工解析 vtable。
    ///      vtable 槽位顺序：Initialize(0) / InitializeExt(1) / GetDefaultParams(2) /
    ///      Uninitialize(3) / EncodeFrame(4) / EncodeParameterSets(5) /
    ///      ForceIntraFrame(6) / SetOption(7) / GetOption(8)。
    ///   3. 结构体按 OpenH264 ≥ 2.3.1 的布局定义；为向前兼容新版，额外保留了
    ///      SEncParamExt/SLayerBSInfo/SSourcePicture 尾部新增的 PSNR 字段
    ///      （旧版 2.3.1 DLL 会忽略这些尾部字段，双向安全）。
    ///   4. 编码参数：baseline（无 B 帧）、iTemporalLayerNum=1（低延迟）、
    ///      GOP=60、码率/分辨率/帧率来自 PhoneBoxConfig。
    /// </summary>
    public sealed class VideoEncoder : IVideoEncoder, IDisposable
    {
        private const int MaxSpatialLayerNum = 4;
        private const int MaxLayerNumOfFrame = 128;
        private const int MaxSlicesNumTmp = 35;

        // EUsageType
        private const int ScreenContentRealTime = 1;

        // RC_MODES
        private const int RcBitrateMode = 1;

        // EProfileIdc / ELevelIdc
        private const int ProfileBaseline = 66;
        private const int LevelUnknown = 0;

        // ECOMPLEXITY_MODE
        private const int LowComplexity = 0;

        // ENCODER_OPTION
        private const int EncoderOptionDataFormat = 0;
        private const int EncoderOptionTraceLevel = 25;

        // EVideoFormatType
        private const int VideoFormatI420 = 23;

        // EVideoFrameType
        private const int VideoFrameTypeSkip = 4;

        // WELS_LOG
        private const int WelsLogQuiet = 0;

        // EParameterSetStrategy
        private const int SpsPpsIncreasingId = 1;

        private static readonly byte[] AnnexBStartCode = { 0x00, 0x00, 0x00, 0x01 };

        private readonly object sync = new object();

        private IntPtr encoder = IntPtr.Zero;
        private IntPtr vtable = IntPtr.Zero;

        private InitializeDelegate initialize;
        private InitializeExtDelegate initializeExt;
        private GetDefaultParamsDelegate getDefaultParams;
        private UninitializeDelegate uninitialize;
        private EncodeFrameDelegate encodeFrame;
        private SetOptionDelegate setOption;

        private IntPtr bsInfoPtr = IntPtr.Zero;
        private int bsInfoSize;

        private byte[] yuvBuffer;
        private int width;
        private int height;
        private int fps;
        private long frameTimestampMs;

        private bool opened;

        public VideoEncoder()
        {
        }

        /// <summary>是否已成功打开编码器。</summary>
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

        /// <summary>打开编码器（I420 输入需要偶数宽高，奇数会自动向下取整）。</summary>
        public void Open(int width, int height, int fps, int bitrate)
        {
            lock (sync)
            {
                if (opened)
                {
                    return;
                }

                // I420 色度采样要求宽高为偶数。
                this.width = (width / 2) * 2;
                this.height = (height / 2) * 2;
                this.fps = Math.Max(1, fps);

                if (this.width <= 0 || this.height <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(width), "编码分辨率必须大于 0。");
                }

                try
                {
                    CreateEncoder();
                    ConfigureEncoder(bitrate);
                    AllocateBuffers();
                    opened = true;
                    frameTimestampMs = 0;
                }
                catch
                {
                    ReleaseEncoder();
                    throw;
                }
            }

            PhoneBoxLog.Info(
                "OpenH264 编码器已打开：" + this.width + "x" + this.height
                + " @" + this.fps + "fps, " + bitrate + "bps。");
        }

        /// <summary>编码一帧 24/32bpp Bitmap，返回 H.264 Annex-B 字节流（无输出时返回 null）。</summary>
        public byte[] Encode(Bitmap frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            lock (sync)
            {
                if (!opened)
                {
                    return null;
                }

                if (frame.Width != width || frame.Height != height)
                {
                    // 输入尺寸不匹配时按编码器尺寸缩放，保证 I420 平面与编码器一致。
                    using (var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(scaled))
                        {
                            g.DrawImage(frame, 0, 0, width, height);
                        }
                        return EncodeCore(scaled);
                    }
                }

                return EncodeCore(frame);
            }
        }

        /// <summary>关闭编码器并释放 native 资源。</summary>
        public void Close()
        {
            lock (sync)
            {
                if (!opened && encoder == IntPtr.Zero)
                {
                    return;
                }
                opened = false;
                ReleaseEncoder();
            }
            PhoneBoxLog.Info("OpenH264 编码器已关闭。");
        }

        public void Dispose()
        {
            Close();
        }

        /// <summary>核心编码流程：BGRA/BGR → I420 → EncodeFrame → 拼装 Annex-B。</summary>
        private byte[] EncodeCore(Bitmap frame)
        {
            ConvertToI420(frame, yuvBuffer, width, height);

            var picture = new SSourcePicture();
            picture.iStride = new int[4];
            picture.pData = new IntPtr[4];
            picture.iColorFormat = VideoFormatI420;
            picture.iStride[0] = width;
            picture.iStride[1] = width / 2;
            picture.iStride[2] = width / 2;
            picture.iStride[3] = 0;
            picture.iPicWidth = width;
            picture.iPicHeight = height;
            picture.uiTimeStamp = frameTimestampMs;
            frameTimestampMs += 1000 / fps;

            int picSize = Marshal.SizeOf(typeof(SSourcePicture));
            IntPtr picPtr = Marshal.AllocHGlobal(picSize);
            var yuvHandle = GCHandle.Alloc(yuvBuffer, GCHandleType.Pinned);
            try
            {
                IntPtr basePtr = yuvHandle.AddrOfPinnedObject();
                int ySize = width * height;
                int uvSize = (width / 2) * (height / 2);
                picture.pData[0] = basePtr;
                picture.pData[1] = IntPtr.Add(basePtr, ySize);
                picture.pData[2] = IntPtr.Add(basePtr, ySize + uvSize);
                picture.pData[3] = IntPtr.Zero;

                // 用非托管内存承载内联布局的 SSourcePicture，再传指针给 native。
                Marshal.StructureToPtr(picture, picPtr, false);

                int rv = encodeFrame(encoder, picPtr, bsInfoPtr);
                if (rv != 0)
                {
                    PhoneBoxLog.Warn("OpenH264 EncodeFrame 返回错误码 " + rv + "。");
                    return null;
                }
            }
            finally
            {
                yuvHandle.Free();
                Marshal.FreeHGlobal(picPtr);
            }

            var info = (SFrameBSInfo)Marshal.PtrToStructure(bsInfoPtr, typeof(SFrameBSInfo));
            return BuildAnnexB(info);
        }

        /// <summary>从 SFrameBSInfo 提取各层 NAL 并拼装为 Annex-B 字节流。</summary>
        /// <remarks>
        /// 实测 OpenH264 的 pBsBuf 输出每个 NAL 已自带 4 字节起始码（00 00 00 01），
        /// 且 pNalLengthInByte 的长度也把起始码计入其中。因此这里直接原样拷贝，
        /// 仅在检测到 NAL 缺失起始码时补一个（兼容不同 OpenH264 版本）。
        /// </remarks>
        private static byte[] BuildAnnexB(SFrameBSInfo info)
        {
            int layerNum = Math.Min(info.iLayerNum, MaxLayerNumOfFrame);
            using (var ms = new MemoryStream())
            {
                for (int i = 0; i < layerNum; i++)
                {
                    var layer = info.sLayerInfo[i];
                    if (layer.eFrameType == VideoFrameTypeSkip || layer.iNalCount <= 0
                        || layer.pBsBuf == IntPtr.Zero || layer.pNalLengthInByte == IntPtr.Zero)
                    {
                        continue;
                    }

                    int srcOffset = 0;
                    for (int n = 0; n < layer.iNalCount; n++)
                    {
                        int len = Marshal.ReadInt32(layer.pNalLengthInByte, n * sizeof(int));
                        if (len <= 0)
                        {
                            continue;
                        }

                        IntPtr nal = IntPtr.Add(layer.pBsBuf, srcOffset);

                        // 检测该 NAL 是否已带 Annex-B 起始码；没有则补上。
                        bool hasStartCode = len >= 4
                            && Marshal.ReadByte(nal, 0) == 0x00
                            && Marshal.ReadByte(nal, 1) == 0x00
                            && Marshal.ReadByte(nal, 2) == 0x00
                            && Marshal.ReadByte(nal, 3) == 0x01;
                        if (!hasStartCode)
                        {
                            ms.Write(AnnexBStartCode, 0, AnnexBStartCode.Length);
                        }

                        var tmp = new byte[len];
                        Marshal.Copy(nal, tmp, 0, len);
                        ms.Write(tmp, 0, len);

                        srcOffset += len;
                    }
                }

                return ms.Length > 0 ? ms.ToArray() : null;
            }
        }

        /// <summary>创建编码器并解析 vtable。</summary>
        private void CreateEncoder()
        {
            int rv = WelsCreateSVCEncoder(ref encoder);
            if (rv != 0 || encoder == IntPtr.Zero)
            {
                throw new InvalidOperationException("WelsCreateSVCEncoder 失败（返回 " + rv + "）。");
            }

            // encoder 是 C++ 对象指针，其首字段为 vtable 指针。
            vtable = Marshal.ReadIntPtr(encoder);

            initialize = GetVtableDelegate<InitializeDelegate>(0);
            initializeExt = GetVtableDelegate<InitializeExtDelegate>(1);
            getDefaultParams = GetVtableDelegate<GetDefaultParamsDelegate>(2);
            uninitialize = GetVtableDelegate<UninitializeDelegate>(3);
            encodeFrame = GetVtableDelegate<EncodeFrameDelegate>(4);
            setOption = GetVtableDelegate<SetOptionDelegate>(7);
        }

        /// <summary>读取 vtable 指定槽位的函数指针并转为委托。</summary>
        private TDelegate GetVtableDelegate<TDelegate>(int slot) where TDelegate : class
        {
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            if (fn == IntPtr.Zero)
            {
                throw new InvalidOperationException("OpenH264 vtable 槽位 " + slot + " 为空。");
            }
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(fn);
        }

        /// <summary>用 GetDefaultParams + 定制参数初始化编码器。</summary>
        private void ConfigureEncoder(int bitrate)
        {
            int size = Marshal.SizeOf(typeof(SEncParamExt));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                // 先拿默认参数（填充 QP / 环路滤波等内部默认值），再覆盖关键项。
                int rv = getDefaultParams(encoder, p);
                if (rv != 0)
                {
                    throw new InvalidOperationException("OpenH264 GetDefaultParams 失败（返回 " + rv + "）。");
                }

                var param = (SEncParamExt)Marshal.PtrToStructure(p, typeof(SEncParamExt));

                param.iUsageType = ScreenContentRealTime;
                param.iPicWidth = width;
                param.iPicHeight = height;
                param.iTargetBitrate = bitrate;
                param.iRCMode = RcBitrateMode;
                param.fMaxFrameRate = fps;

                // 单时间层 = 无 B 帧；单空间层；baseline profile。
                param.iTemporalLayerNum = 1;
                param.iSpatialLayerNum = 1;
                param.iComplexityMode = LowComplexity;
                param.uiIntraPeriod = 30;           // 1 秒一个 IDR（30fps），手机丢包后快速恢复
                param.iNumRefFrame = 1;             // 单参考帧，低延迟
                param.bEnableFrameSkip = true;      // 码率过高时允许丢帧

                // 关键：INCREASING_ID 让 SPS/PPS 在每个 IDR 重发，UDP 丢包后手机也能恢复解码。
                // （CONSTANT_ID 只在首帧发一次 SPS/PPS，一旦首包丢失，手机将一直黑屏。）
                param.eSpsPpsIdStrategy = SpsPpsIncreasingId;

                var layer = param.sSpatialLayers[0];
                layer.iVideoWidth = width;
                layer.iVideoHeight = height;
                layer.fFrameRate = fps;
                layer.iSpatialBitrate = bitrate;
                layer.iMaxSpatialBitrate = bitrate;
                layer.uiProfileIdc = ProfileBaseline;
                layer.uiLevelIdc = LevelUnknown;
                layer.sSliceArgument.uiSliceMode = 0; // SM_SINGLE_SLICE
                param.sSpatialLayers[0] = layer;

                Marshal.StructureToPtr(param, p, false);

                rv = initializeExt(encoder, p);
                if (rv != 0)
                {
                    throw new InvalidOperationException("OpenH264 InitializeExt 失败（返回 " + rv + "）。");
                }

                // 明确输入为 I420。
                SetIntOption(EncoderOptionDataFormat, VideoFormatI420);
                // 静默 OpenH264 内部日志，避免污染 stderr。
                SetIntOption(EncoderOptionTraceLevel, WelsLogQuiet);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>通过 SetOption 设置一个 int 选项。</summary>
        private void SetIntOption(int option, int value)
        {
            IntPtr p = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(p, value);
                int rv = setOption(encoder, option, p);
                if (rv != 0)
                {
                    PhoneBoxLog.Warn("OpenH264 SetOption(" + option + ") 返回错误码 " + rv + "。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>分配 I420 缓冲与 SFrameBSInfo 输出缓冲。</summary>
        private void AllocateBuffers()
        {
            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);
            yuvBuffer = new byte[ySize + uvSize + uvSize];

            bsInfoSize = Marshal.SizeOf(typeof(SFrameBSInfo));
            bsInfoPtr = Marshal.AllocHGlobal(bsInfoSize);
            // 清零，避免未使用的 layer 数据残留。
            for (int i = 0; i < bsInfoSize; i++)
            {
                Marshal.WriteByte(bsInfoPtr, i, 0);
            }
        }

        /// <summary>释放编码器与 native 缓冲。</summary>
        private void ReleaseEncoder()
        {
            if (bsInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(bsInfoPtr);
                bsInfoPtr = IntPtr.Zero;
            }

            if (encoder != IntPtr.Zero)
            {
                if (uninitialize != null)
                {
                    try
                    {
                        uninitialize(encoder);
                    }
                    catch (Exception ex)
                    {
                        PhoneBoxLog.Warn("OpenH264 Uninitialize 失败：" + ex.Message);
                    }
                }
                WelsDestroySVCEncoder(encoder);
                encoder = IntPtr.Zero;
            }

            vtable = IntPtr.Zero;
            initialize = null;
            initializeExt = null;
            getDefaultParams = null;
            uninitialize = null;
            encodeFrame = null;
            setOption = null;
            yuvBuffer = null;
        }

        /// <summary>把 24/32bpp Bitmap（内存序 BGR/BGRA）转成 I420 平面。</summary>
        private static unsafe void ConvertToI420(Bitmap frame, byte[] yuv, int w, int h)
        {
            var rect = new Rectangle(0, 0, w, h);
            var data = frame.LockBits(rect, ImageLockMode.ReadOnly, frame.PixelFormat);
            try
            {
                int bpp = GetBytesPerPixel(frame.PixelFormat);
                byte* src = (byte*)data.Scan0;
                int srcStride = data.Stride;
                int ySize = w * h;
                int uvSize = (w / 2) * (h / 2);

                fixed (byte* pYuv = yuv)
                {
                    byte* pY = pYuv;
                    byte* pU = pYuv + ySize;
                    byte* pV = pYuv + ySize + uvSize;

                    for (int y = 0; y < h; y++)
                    {
                        byte* row = src + y * srcStride;
                        byte* yRow = pY + y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = x * bpp;
                            yRow[x] = RgbToY(row[i + 2], row[i + 1], row[i]);
                        }
                    }

                    for (int y = 0; y < h; y += 2)
                    {
                        for (int x = 0; x < w; x += 2)
                        {
                            int sumR = 0;
                            int sumG = 0;
                            int sumB = 0;

                            for (int dy = 0; dy < 2; dy++)
                            {
                                byte* row = src + (y + dy) * srcStride;
                                for (int dx = 0; dx < 2; dx++)
                                {
                                    int i = (x + dx) * bpp;
                                    sumB += row[i];
                                    sumG += row[i + 1];
                                    sumR += row[i + 2];
                                }
                            }

                            int r = sumR >> 2;
                            int g = sumG >> 2;
                            int b = sumB >> 2;
                            int uvIdx = (y >> 1) * (w >> 1) + (x >> 1);
                            pU[uvIdx] = RgbToU(r, g, b);
                            pV[uvIdx] = RgbToV(r, g, b);
                        }
                    }
                }
            }
            finally
            {
                frame.UnlockBits(data);
            }
        }

        private static int GetBytesPerPixel(PixelFormat format)
        {
            switch (format)
            {
                case PixelFormat.Format24bppRgb:
                    return 3;
                case PixelFormat.Format32bppRgb:
                case PixelFormat.Format32bppArgb:
                case PixelFormat.Format32bppPArgb:
                    return 4;
                default:
                    throw new NotSupportedException("不支持的像素格式：" + format);
            }
        }

        // BT.601 全范围整数近似（避免浮点）。
        private static byte RgbToY(int r, int g, int b)
        {
            int v = (66 * r + 129 * g + 25 * b + 128) >> 8;
            return ClampByte(v);
        }

        private static byte RgbToU(int r, int g, int b)
        {
            int v = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            return ClampByte(v);
        }

        private static byte RgbToV(int r, int g, int b)
        {
            int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
            return ClampByte(v);
        }

        private static byte ClampByte(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        // ---- P/Invoke ----

        [DllImport("openh264", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WelsCreateSVCEncoder(ref IntPtr ppEncoder);

        [DllImport("openh264", CallingConvention = CallingConvention.Cdecl)]
        private static extern void WelsDestroySVCEncoder(IntPtr pEncoder);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeDelegate(IntPtr self, IntPtr pParam);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeExtDelegate(IntPtr self, IntPtr pParam);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDefaultParamsDelegate(IntPtr self, IntPtr pParam);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UninitializeDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EncodeFrameDelegate(IntPtr self, IntPtr kpSrcPic, IntPtr pBsInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetOptionDelegate(IntPtr self, int eOptionId, IntPtr pOption);

        // ---- OpenH264 结构体（布局匹配 codec/app_def.h，见类注释） ----

        [StructLayout(LayoutKind.Sequential)]
        private struct SSliceArgument
        {
            public int uiSliceMode;
            public uint uiSliceNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxSlicesNumTmp)]
            public uint[] uiSliceMbNum;
            public uint uiSliceSizeConstraint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SSpatialLayerConfig
        {
            public int iVideoWidth;
            public int iVideoHeight;
            public float fFrameRate;
            public int iSpatialBitrate;
            public int iMaxSpatialBitrate;
            public int uiProfileIdc;
            public int uiLevelIdc;
            public int iDLayerQp;
            public SSliceArgument sSliceArgument;

            [MarshalAs(UnmanagedType.I1)] public bool bVideoSignalTypePresent;
            public byte uiVideoFormat;
            [MarshalAs(UnmanagedType.I1)] public bool bFullRange;
            [MarshalAs(UnmanagedType.I1)] public bool bColorDescriptionPresent;
            public byte uiColorPrimaries;
            public byte uiTransferCharacteristics;
            public byte uiColorMatrix;
            [MarshalAs(UnmanagedType.I1)] public bool bAspectRatioPresent;
            public int eAspectRatio;
            public ushort sAspectRatioExtWidth;
            public ushort sAspectRatioExtHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SEncParamExt
        {
            public int iUsageType;
            public int iPicWidth;
            public int iPicHeight;
            public int iTargetBitrate;
            public int iRCMode;
            public float fMaxFrameRate;

            public int iTemporalLayerNum;
            public int iSpatialLayerNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxSpatialLayerNum)]
            public SSpatialLayerConfig[] sSpatialLayers;

            public int iComplexityMode;
            public uint uiIntraPeriod;
            public int iNumRefFrame;
            public int eSpsPpsIdStrategy;

            [MarshalAs(UnmanagedType.I1)] public bool bPrefixNalAddingCtrl;
            [MarshalAs(UnmanagedType.I1)] public bool bEnableSSEI;
            [MarshalAs(UnmanagedType.I1)] public bool bSimulcastAVC;
            public int iPaddingFlag;
            public int iEntropyCodingModeFlag;

            [MarshalAs(UnmanagedType.I1)] public bool bEnableFrameSkip;
            public int iMaxBitrate;
            public int iMaxQp;
            public int iMinQp;
            public uint uiMaxNalSize;

            [MarshalAs(UnmanagedType.I1)] public bool bEnableLongTermReference;
            public int iLTRRefNum;
            public uint iLtrMarkPeriod;

            public ushort iMultipleThreadIdc;
            [MarshalAs(UnmanagedType.I1)] public bool bUseLoadBalancing;

            public int iLoopFilterDisableIdc;
            public int iLoopFilterAlphaC0Offset;
            public int iLoopFilterBetaOffset;

            [MarshalAs(UnmanagedType.I1)] public bool bEnableDenoise;
            [MarshalAs(UnmanagedType.I1)] public bool bEnableBackgroundDetection;
            [MarshalAs(UnmanagedType.I1)] public bool bEnableAdaptiveQuant;
            [MarshalAs(UnmanagedType.I1)] public bool bEnableFrameCroppingFlag;
            [MarshalAs(UnmanagedType.I1)] public bool bEnableSceneChangeDetect;
            [MarshalAs(UnmanagedType.I1)] public bool bIsLosslessLink;
            [MarshalAs(UnmanagedType.I1)] public bool bFixRCOverShoot;
            public int iIdrBitrateRatio;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SSourcePicture
        {
            public int iColorFormat;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public int[] iStride;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public IntPtr[] pData;
            public int iPicWidth;
            public int iPicHeight;
            public long uiTimeStamp;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SLayerBSInfo
        {
            public byte uiTemporalId;
            public byte uiSpatialId;
            public byte uiQualityId;
            public int eFrameType;
            public byte uiLayerType;
            public int iSubSeqId;
            public int iNalCount;
            public IntPtr pNalLengthInByte;
            public IntPtr pBsBuf;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SFrameBSInfo
        {
            public int iLayerNum;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxLayerNumOfFrame)]
            public SLayerBSInfo[] sLayerInfo;
            public int eFrameType;
            public int iFrameSizeInBytes;
            public long uiTimeStamp;
        }
    }
}
