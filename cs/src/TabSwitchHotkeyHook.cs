using System;
using System.Runtime.InteropServices;
using YeyouPlusPlus.PhoneBox;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 窗口级标签切换快捷键钩子（WH_KEYBOARD_LL）。
    ///
    /// 主窗口把 WinForms 版 CEF 通过 HwndHost 嵌入 WPF，当网页（或地址栏以外的任何输入框）
    /// 获得键盘焦点时，WPF 的 PreviewKeyDown / InputBindings 收不到这些按键，因此用全局低级
    /// 键盘钩子来捕获快捷键，保证无论焦点在 WPF 控件还是 CEF 网页里都能切换标签。
    ///
    /// 键位：Ctrl+Tab / Ctrl+PageDown（下一个）、Ctrl+PageUp（上一个）、Ctrl+1~8（第 1~8 个）、
    /// Ctrl+9（最后一个）。刻意不用 Ctrl+Shift+Tab：中文 Windows 下 Ctrl+Shift 会触发输入法切换，
    /// 与快捷键冲突。
    ///
    /// 仅在主窗口位于前台（GetForegroundWindow == 主窗口 HWND）且未按 Alt 时才处理，
    /// 命中后返回 1 拦截该按键，避免网页 / 地址栏再收到；其余按键一律原样放行。
    /// </summary>
    public sealed class TabSwitchHotkeyHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_TAB = 0x09;
        private const int VK_PRIOR = 0x21; // Page Up
        private const int VK_NEXT = 0x22;  // Page Down
        private const int VK_1 = 0x31;
        private const int VK_9 = 0x39;

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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly Func<IntPtr> ownerHandleProvider;
        private readonly Action onNextTab;
        private readonly Action onPreviousTab;
        private readonly Action<int> onGotoTab;

        private readonly object sync = new object();
        private LowLevelKeyboardProc proc;
        private IntPtr hook = IntPtr.Zero;
        private IntPtr ownerHandle = IntPtr.Zero;

        /// <summary>
        /// 创建标签切换快捷键钩子。
        /// </summary>
        /// <param name="ownerHandleProvider">返回主窗口 HWND（仅该窗口前台时才触发）。</param>
        /// <param name="onNextTab">Ctrl+Tab / Ctrl+PageDown 回调。</param>
        /// <param name="onPreviousTab">Ctrl+PageUp 回调。</param>
        /// <param name="onGotoTab">Ctrl+1~9 回调，参数为 0 基标签索引，-1 表示最后一个标签。</param>
        public TabSwitchHotkeyHook(Func<IntPtr> ownerHandleProvider,
            Action onNextTab, Action onPreviousTab, Action<int> onGotoTab)
        {
            this.ownerHandleProvider = ownerHandleProvider ?? throw new ArgumentNullException(nameof(ownerHandleProvider));
            this.onNextTab = onNextTab;
            this.onPreviousTab = onPreviousTab;
            this.onGotoTab = onGotoTab;
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

                ownerHandle = ownerHandleProvider();

                // 必须持有委托引用，避免被 GC 回收导致回调地址失效。
                proc = HookCallback;
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero)
                {
                    // 安装失败（如被安全软件拦截）不能静默，记录日志便于诊断。
                    PhoneBoxLog.Warn("标签切换快捷键钩子安装失败：SetWindowsHookEx 返回 NULL，GetLastError=" +
                        Marshal.GetLastWin32Error());
                }
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
                ownerHandle = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    // 仅当主窗口前台时处理，避免抢走其它应用的快捷键。
                    if (ownerHandle != IntPtr.Zero && GetForegroundWindow() == ownerHandle)
                    {
                        var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        // 上述快捷键均为明确的 Ctrl 修饰键组合，可放心拦截；
                        // 同时按下 Alt（如 Ctrl+Alt+Tab 任务切换）时不处理，避免与系统冲突。
                        if (IsKeyDown(VK_CONTROL) && !IsKeyDown(VK_MENU) && TryHandle(info.vkCode))
                        {
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        private bool TryHandle(uint vkCode)
        {
            if (vkCode == VK_TAB || vkCode == VK_NEXT) // Ctrl+Tab / Ctrl+PageDown → 下一个
            {
                onNextTab?.Invoke();
                return true;
            }

            if (vkCode == VK_PRIOR) // Ctrl+PageUp → 上一个
            {
                onPreviousTab?.Invoke();
                return true;
            }

            if (vkCode >= VK_1 && vkCode <= VK_9)
            {
                // Ctrl+1~8 对应第 1~8 个标签（索引 0~7）；Ctrl+9 特殊映射到最后一个标签（传 -1）。
                int index = vkCode == VK_9 ? -1 : (int)(vkCode - VK_1);
                onGotoTab?.Invoke(index);
                return true;
            }

            return false;
        }

        private static bool IsKeyDown(int vkCode)
        {
            return (GetAsyncKeyState(vkCode) & 0x8000) != 0;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
