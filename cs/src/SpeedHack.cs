using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 内置变速齿轮：通过命名共享内存下发倍率 + 注入 speedhack.dll 到
    /// Flash 子进程（PPAPI 进程），hook 系统时间 API 实现倍速。
    /// </summary>
    public static class SpeedHack
    {
        private const string SharedMemoryName = @"Local\YeyouSpeedHack";
        private const uint PROCESS_ALL_ACCESS = 0x1FFFFF;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_READWRITE = 0x04;

        private static IntPtr _mapping;
        private static IntPtr _view;
        private static double _currentSpeed = 1.0;
        private static readonly object Lock = new object();

        /// <summary>当前倍率。</summary>
        public static double CurrentSpeed => _currentSpeed;

        /// <summary>设置倍率（写入共享内存，并确保 Flash 子进程已注入）。</summary>
        public static void SetSpeed(double speed)
        {
            lock (Lock)
            {
                _currentSpeed = speed;
                EnsureSharedMemory();
                WriteSpeed(speed);
                InjectToFlashProcesses();
            }
        }

        // ============ 共享内存（倍率下发） ============

        private static void EnsureSharedMemory()
        {
            if (_view != IntPtr.Zero)
            {
                return;
            }
            _mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, 8, SharedMemoryName);
            if (_mapping == IntPtr.Zero)
            {
                return;
            }
            _view = MapViewOfFile(_mapping, 0x0002 /*FILE_MAP_WRITE*/, 0, 0, 8);
        }

        private static unsafe void WriteSpeed(double speed)
        {
            if (_view == IntPtr.Zero)
            {
                return;
            }
            *(double*)_view = speed;
        }

        // ============ DLL 注入 ============

        private static string DllPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "speedhack.dll");

        private static void InjectToFlashProcesses()
        {
            if (!File.Exists(DllPath))
            {
                return;
            }

            foreach (var proc in Process.GetProcessesByName("CefSharp.BrowserSubprocess"))
            {
                try
                {
                    if (_injectedPids.Contains(proc.Id))
                    {
                        continue;
                    }
                    // 关键：只在 pepflashplayer.dll 已加载的进程里注入。
                    // 否则 DLL 注入后 Flash 尚未就绪，会因等待超时而永久错过 hook
                    // （表现为进入副本后变速失效）。
                    if (!HasFlashModule(proc.Id))
                    {
                        continue;
                    }
                    if (Inject(proc.Id, DllPath))
                    {
                        _injectedPids.Add(proc.Id);
                    }
                }
                catch
                {
                    // 注入失败（权限等），跳过该进程。
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        private static readonly System.Collections.Generic.HashSet<int> _injectedPids
            = new System.Collections.Generic.HashSet<int>();

        /// <summary>判断进程是否已加载 pepflashplayer.dll（Toolhelp32 枚举模块）。</summary>
        private static bool HasFlashModule(int pid)
        {
            const uint TH32CS_SNAPMODULE = 0x00000008;
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, (uint)pid);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1))
            {
                return false;
            }
            try
            {
                var me = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32)) };
                if (!Module32First(snap, ref me))
                {
                    return false;
                }
                do
                {
                    if (me.szModule != null &&
                        me.szModule.IndexOf("pepflashplayer", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                } while (Module32Next(snap, ref me));
                return false;
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MODULEENTRY32
        {
            public uint dwSize;
            public uint th32ModuleID;
            public uint th32ProcessID;
            public uint GlblcntUsage;
            public uint ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExePath;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

        private static bool Inject(int pid, string dllPath)
        {
            IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // 在目标进程分配内存并写入 DLL 路径（UTF-16）。
                byte[] pathBytes = new byte[(dllPath.Length + 1) * 2];
                Buffer.BlockCopy(dllPath.ToCharArray(), 0, pathBytes, 0, dllPath.Length * 2);

                IntPtr remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero,
                    (uint)pathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (remoteMem == IntPtr.Zero)
                {
                    return false;
                }

                if (!WriteProcessMemory(hProcess, remoteMem, pathBytes,
                    (uint)pathBytes.Length, out _))
                {
                    return false;
                }

                // 取 LoadLibraryW 地址，远程线程调用它加载 DLL。
                IntPtr loadLibraryAddr = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
                if (loadLibraryAddr == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0,
                    loadLibraryAddr, remoteMem, 0, out _);
                if (hThread == IntPtr.Zero)
                {
                    return false;
                }

                WaitForSingleObject(hThread, 5000);
                CloseHandle(hThread);
                return true;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        // ============ Win32 ============

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMapping(IntPtr hFile, IntPtr lpAttributes,
            uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMapping, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
            uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
            uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out IntPtr lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
