using System;
using System.Runtime.InteropServices;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 全局低级键盘钩子（WH_KEYBOARD_LL）。
    ///
    /// 用于全屏沉浸式：捕获 Esc 按下并拦截（返回 1，不让 Esc 继续传递），
    /// 回调 <c>onEsc</c>。仅在沉浸式期间挂载，退出即卸载。
    ///
    /// 说明：WH_KEYBOARD_LL 属于全局钩子，但 Windows 会通过消息循环把按键事件
    /// 派发到「安装钩子的线程」，因此回调在调用 <see cref="Start"/> 的 UI 线程执行，
    /// 可直接安全地操作 WPF 控件。
    /// </summary>
    public sealed class LowLevelKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int VK_ESCAPE = 0x1B;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly Action onEsc;
        private readonly object sync = new object();
        private LowLevelKeyboardProc proc;
        private IntPtr hook = IntPtr.Zero;

        public LowLevelKeyboardHook(Action onEsc)
        {
            this.onEsc = onEsc ?? throw new ArgumentNullException(nameof(onEsc));
        }

        /// <summary>钩子是否已挂载。</summary>
        public bool IsHooked
        {
            get { lock (sync) { return hook != IntPtr.Zero; } }
        }

        /// <summary>挂载钩子（幂等）。</summary>
        public void Start()
        {
            lock (sync)
            {
                if (hook != IntPtr.Zero)
                {
                    return;
                }

                // 必须持有委托引用，避免被 GC 回收导致回调地址失效。
                proc = HookCallback;
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            }
        }

        /// <summary>卸载钩子（幂等）。</summary>
        public void Stop()
        {
            lock (sync)
            {
                if (hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(hook);
                    hook = IntPtr.Zero;
                }
                proc = null;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    if (info.vkCode == VK_ESCAPE)
                    {
                        onEsc?.Invoke();
                        // 返回 1：拦截该按键，不再传递给后续钩子 / 目标窗口。
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
