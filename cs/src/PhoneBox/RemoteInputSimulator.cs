using System;
using System.Runtime.InteropServices;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 基于 Win32 SendInput 的按键模拟器（双人输入隔离的核心）。
    ///
    /// 双人隔离策略：
    ///   本机键盘 = 玩家 1，手机虚拟按键 = 玩家 2。
    ///   手机按键统一用【扫描码】注入（KEYEVENTF_SCANCODE），而不是虚拟键，
    ///   这样能绕开输入法、前台焦点与不同键盘布局的差异，保证按键落到游戏内部。
    ///
    /// 键位映射空间分离：玩家 2 默认使用方向键 + 小键盘 + Z/X/C 等键，
    ///   避开玩家 1 常用的 WASD / 空格 / JKL 等主键区，从源头上减少两方按键互相干扰。
    /// </summary>
    public static class RemoteInputSimulator
    {
        private const int InputKeyboard = 1;

        private const uint KeyEventFExtendedKey = 0x0001;
        private const uint KeyEventFKeyUp = 0x0002;
        private const uint KeyEventFScanCode = 0x0008;

        private const uint MapVkVkToVsc = 0;

        /// <summary>
        /// 鼠标输入（union 成员，本类未使用，但为 INPUT 结构体的正确布局而定义）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>键盘输入。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>硬件输入（union 成员，本类未使用，但为 INPUT 结构体的正确布局而定义）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        /// <summary>
        /// Win32 INPUT 结构体。使用显式布局实现 union：
        /// type 占 4 字节；x64 下 union 因含指针而对齐到 8 字节，故三个成员都从偏移 8 开始。
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct Input
        {
            [FieldOffset(0)]
            public int type;

            [FieldOffset(8)]
            public MouseInput mi;

            [FieldOffset(8)]
            public KeyboardInput ki;

            [FieldOffset(8)]
            public HardwareInput hi;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>按下扫描码对应的键。</summary>
        public static void KeyDown(ushort scanCode)
        {
            SendKey(scanCode, keyUp: false, extended: IsExtendedScanCode(scanCode));
        }

        /// <summary>按下扫描码对应的键（显式指定是否扩展键）。</summary>
        public static void KeyDown(ushort scanCode, bool extended)
        {
            SendKey(scanCode, keyUp: false, extended: extended);
        }

        /// <summary>抬起扫描码对应的键。</summary>
        public static void KeyUp(ushort scanCode)
        {
            SendKey(scanCode, keyUp: true, extended: IsExtendedScanCode(scanCode));
        }

        /// <summary>抬起扫描码对应的键（显式指定是否扩展键）。</summary>
        public static void KeyUp(ushort scanCode, bool extended)
        {
            SendKey(scanCode, keyUp: true, extended: extended);
        }

        /// <summary>点按（按下后立即抬起）。</summary>
        public static void Tap(ushort scanCode)
        {
            KeyDown(scanCode);
            KeyUp(scanCode);
        }

        /// <summary>点按（显式指定是否扩展键）。</summary>
        public static void Tap(ushort scanCode, bool extended)
        {
            SendKey(scanCode, keyUp: false, extended: extended);
            SendKey(scanCode, keyUp: true, extended: extended);
        }

        /// <summary>
        /// 由虚拟键码换算硬件扫描码（MAPVK_VK_TO_VSC）。
        /// 手机端若只上报 KeyCode 而未上报 ScanCode，则由调用方据此换算。
        /// </summary>
        public static ushort ScanCodeFromVirtualKey(int virtualKey)
        {
            if (virtualKey <= 0)
            {
                return 0;
            }

            uint scanCode = MapVirtualKey((uint)virtualKey, MapVkVkToVsc);
            return scanCode == 0 ? (ushort)0 : (ushort)scanCode;
        }

        /// <summary>发送一次键盘输入事件。</summary>
        private static void SendKey(ushort scanCode, bool keyUp, bool extended)
        {
            var input = new Input();
            input.type = InputKeyboard;
            input.ki.wVk = 0; // 使用扫描码时 wVk 填 0
            input.ki.wScan = scanCode;
            input.ki.dwFlags = KeyEventFScanCode
                | (keyUp ? KeyEventFKeyUp : 0U)
                | (extended ? KeyEventFExtendedKey : 0U);
            input.ki.time = 0;
            input.ki.dwExtraInfo = IntPtr.Zero;

            uint sent = SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input)));
            if (sent != 1)
            {
                int error = Marshal.GetLastWin32Error();
                PhoneBoxLog.Warn(
                    "SendInput 失败：扫描码 0x" + scanCode.ToString("X") + "，错误码 " + error + "。");
            }
        }

        /// <summary>
        /// 判断扫描码是否属于“扩展键”（硬件扫描码带 E0 前缀的键）。
        /// 方向键 / 编辑区键与右侧修饰键需要 KEYEVENTF_EXTENDEDKEY 标志才能被正确识别。
        /// 注意：小键盘部分键与编辑区共享扫描码，本策略优先保证方向键正确注入，
        /// 如需精确区分小键盘，可调用带 extended 参数的显式重载。
        /// </summary>
        private static bool IsExtendedScanCode(ushort scanCode)
        {
            switch (scanCode)
            {
                case 0x47: // Home / Numpad 7
                case 0x48: // Up   / Numpad 8
                case 0x49: // PgUp / Numpad 9
                case 0x4B: // Left / Numpad 4
                case 0x4D: // Right/ Numpad 6
                case 0x4F: // End  / Numpad 1
                case 0x50: // Down / Numpad 2
                case 0x51: // PgDn / Numpad 3
                case 0x52: // Ins  / Numpad 0
                case 0x53: // Del  / Numpad .
                case 0x5B: // Left Win
                case 0x5C: // Right Win
                case 0x5D: // Menu
                    return true;
                default:
                    return false;
            }
        }
    }
}
