using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 全局统一音量控制器。
    ///
    /// 通过 Windows Core Audio 控制「本进程」全部音频会话的主音量 / 静音。
    /// CefSharp 的 CEF 子进程与主进程同名（均为 YeyouPlusPlus.exe，仅 PID 不同），
    /// 因此按进程名匹配即可覆盖浏览器 / Flash / 背景视频等所有音频会话。
    ///
    /// 音量与静音状态作为进程级单例缓存，<see cref="Apply"/> 会把缓存值应用到
    /// 当前枚举到的每一个本进程会话，从而让「新出现的会话」也继承统一音量（粘住）。
    /// </summary>
    public static class AudioVolumeController
    {
        // eRender = 0（渲染/播放端点），eMultimedia = 1（多媒体角色）。
        private const int E_RENDER = 0;
        private const int E_MULTIMEDIA = 1;

        // CLSCTX_ALL：激活进程内 / 本地 / 远程服务器均可。
        private const int CLSCTX_ALL = 23;

        private const string ClsidMmDeviceEnumerator = "BCDE0395-E52F-467C-8E3D-C4579291692E";

        private static readonly object Sync = new object();
        private static float volume = 1f; // 0.0 ~ 1.0
        private static bool muted;

        /// <summary>当前缓存的目标音量（0.0~1.0）。</summary>
        public static float Volume
        {
            get { lock (Sync) { return volume; } }
        }

        /// <summary>当前缓存的目标静音状态。</summary>
        public static bool Muted
        {
            get { lock (Sync) { return muted; } }
        }

        /// <summary>设置全局音量（0.0~1.0，自动夹紧）并立即应用。</summary>
        public static void SetVolume(float value)
        {
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;
            lock (Sync) { volume = value; }
            Apply();
        }

        /// <summary>设置全局静音并立即应用。</summary>
        public static void SetMuted(bool value)
        {
            lock (Sync) { muted = value; }
            Apply();
        }

        /// <summary>
        /// 枚举默认多媒体渲染端点上本进程的全部音频会话，并把缓存音量 / 静音应用上去。
        /// 失败（无音频设备、会话已退出等）静默忽略，保证任何环境下不崩溃。
        /// </summary>
        public static void Apply()
        {
            float targetVolume;
            bool targetMuted;
            lock (Sync)
            {
                targetVolume = volume;
                targetMuted = muted;
            }

            string currentName = null;
            try
            {
                currentName = Process.GetCurrentProcess().ProcessName;
            }
            catch
            {
                // 取不到本进程名则放弃本次应用。
                return;
            }

            try
            {
                Type enumType = Type.GetTypeFromCLSID(new Guid(ClsidMmDeviceEnumerator));
                if (enumType == null)
                {
                    return;
                }

                object enumObj = Activator.CreateInstance(enumType);
                var enumerator = enumObj as IMMDeviceEnumerator;
                if (enumerator == null)
                {
                    return;
                }

                if (enumerator.GetDefaultAudioEndpoint(E_RENDER, E_MULTIMEDIA, out IMMDevice device) != 0)
                {
                    return;
                }

                // IAudioSessionManager2：77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F。
                Guid mgrIid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                if (device.Activate(ref mgrIid, CLSCTX_ALL, IntPtr.Zero, out object mgrObj) != 0)
                {
                    return;
                }

                var manager = mgrObj as IAudioSessionManager2;
                if (manager == null)
                {
                    return;
                }

                if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum) != 0)
                {
                    return;
                }

                if (sessionEnum.GetCount(out int count) != 0)
                {
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    ApplyToSession(sessionEnum, i, currentName, targetVolume, targetMuted);
                }
            }
            catch
            {
                // 音频端点枚举失败（如系统无音频设备）静默忽略，不影响主流程。
            }
        }

        /// <summary>对第 index 个会话：确认属于本进程后，写入音量与静音。</summary>
        private static void ApplyToSession(IAudioSessionEnumerator sessionEnum, int index,
            string currentName, float targetVolume, bool targetMuted)
        {
            try
            {
                if (sessionEnum.GetSession(index, out IAudioSessionControl control) != 0)
                {
                    return;
                }

                var control2 = control as IAudioSessionControl2;
                if (control2 == null)
                {
                    return;
                }

                if (control2.GetProcessId(out uint pid) != 0)
                {
                    return;
                }

                string name = null;
                try
                {
                    using (var p = Process.GetProcessById((int)pid))
                    {
                        name = p.ProcessName;
                    }
                }
                catch
                {
                    // 会话对应进程已退出，跳过。
                }

                if (string.IsNullOrEmpty(name) ||
                    !string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var simple = control2 as ISimpleAudioVolume;
                if (simple == null)
                {
                    return;
                }

                // EventContext 传 Guid.Empty：不需要与其他客户端同步音量子组。
                Guid emptyContext = Guid.Empty;
                simple.SetMasterVolume(targetVolume, ref emptyContext);
                simple.SetMute(targetMuted, ref emptyContext);
            }
            catch
            {
                // 单个会话失败（进程退出 / 会话关闭等）跳过，继续处理其余会话。
            }
        }

        // ================= Core Audio COM 接口声明 =================
        // 仅实际调用的方法使用 [PreserveSig] + 原始 HRESULT 返回；其余槽位用空 stub
        // 占位以保持 vtable 顺序正确（顺序错误会导致 QueryInterface 后调用到错误槽位）。

        /// <summary>IMMDeviceEnumerator：A95664D2-9614-4F35-A746-DE8DB63617E6。</summary>
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            void NotImpl1(); // EnumAudioEndpoints

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);

            void NotImpl2(); // GetDevice
            void NotImpl3(); // RegisterEndpointNotificationCallback
            void NotImpl4(); // UnregisterEndpointNotificationCallback
        }

        /// <summary>IMMDevice：D666063F-1587-4E43-81F1-B948E807363F。</summary>
        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

            void NotImpl1(); // OpenPropertyStore
            void NotImpl2(); // GetId
            void NotImpl3(); // GetState
        }

        /// <summary>IAudioSessionManager2：77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F（继承 IAudioSessionManager）。</summary>
        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionManager2
        {
            void NotImpl1(); // GetAudioSessionControl
            void NotImpl2(); // GetSimpleAudioVolume

            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

            void NotImpl3(); // RegisterSessionNotification
            void NotImpl4(); // UnregisterSessionNotification
            void NotImpl5(); // RegisterDuckNotification
            void NotImpl6(); // UnregisterDuckNotification
        }

        /// <summary>IAudioSessionEnumerator：E2F5BB11-0570-40CA-ACDD-3AA01277DEE8。</summary>
        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int sessionCount);

            [PreserveSig]
            int GetSession(int sessionIndex, out IAudioSessionControl session);
        }

        /// <summary>IAudioSessionControl：F4B1A599-7266-4319-A8CA-E70ACB11E8CD。</summary>
        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionControl
        {
            void NotImpl1(); // GetState
            void NotImpl2(); // GetDisplayName
            void NotImpl3(); // SetDisplayName
            void NotImpl4(); // GetIconPath
            void NotImpl5(); // SetIconPath
            void NotImpl6(); // GetGroupingParam
            void NotImpl7(); // SetGroupingParam
            void NotImpl8(); // RegisterAudioSessionNotification
            void NotImpl9(); // UnregisterAudioSessionNotification
        }

        /// <summary>IAudioSessionControl2：bfb7ff88-7239-4fc9-8fa2-07c950be9c6d（继承 IAudioSessionControl）。</summary>
        [ComImport]
        [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioSessionControl2
        {
            void NotImpl1(); // GetState
            void NotImpl2(); // GetDisplayName
            void NotImpl3(); // SetDisplayName
            void NotImpl4(); // GetIconPath
            void NotImpl5(); // SetIconPath
            void NotImpl6(); // GetGroupingParam
            void NotImpl7(); // SetGroupingParam
            void NotImpl8(); // RegisterAudioSessionNotification
            void NotImpl9(); // UnregisterAudioSessionNotification
            void NotImpl10(); // GetSessionIdentifier
            void NotImpl11(); // GetSessionInstanceIdentifier

            [PreserveSig]
            int GetProcessId(out uint processId);

            void NotImpl12(); // IsSystemSoundsSession
            void NotImpl13(); // SetDuckingPreference
        }

        /// <summary>ISimpleAudioVolume：87CE5498-68D6-44E5-9215-6DA47EF883D8。</summary>
        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleAudioVolume
        {
            [PreserveSig]
            int SetMasterVolume(float level, ref Guid eventContext);

            [PreserveSig]
            int GetMasterVolume(out float level);

            [PreserveSig]
            int SetMute(bool isMuted, ref Guid eventContext);

            [PreserveSig]
            int GetMute(out bool isMuted);
        }
    }
}
