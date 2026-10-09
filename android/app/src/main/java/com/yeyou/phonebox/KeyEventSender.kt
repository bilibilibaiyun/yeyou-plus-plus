package com.yeyou.phonebox

/**
 * 按键事件发送器：把虚拟按键按下/松开转换为 Key 消息，通过控制通道发送。
 *
 * 映射策略：虚拟按键直接携带 Windows 虚拟键码（Virtual-Key Code），扫描码填 0。
 * 电脑端 `PhoneBoxServer.OnKeyReceived` 优先使用 ScanCode，缺失时调用
 * `RemoteInputSimulator.ScanCodeFromVirtualKey(KeyCode)`（MapVirtualKey 兜底）换算，
 * 因此手机端无需自行维护「Android keyCode → 扫描码」映射。
 */
class KeyEventSender(private val channel: ControlChannel?) {

    /** 按下虚拟按键。 */
    fun sendDown(virtualKeyCode: Int) {
        channel?.sendKey(keyCode = virtualKeyCode, scanCode = 0, isDown = true)
    }

    /** 松开虚拟按键。 */
    fun sendUp(virtualKeyCode: Int) {
        channel?.sendKey(keyCode = virtualKeyCode, scanCode = 0, isDown = false)
    }
}
