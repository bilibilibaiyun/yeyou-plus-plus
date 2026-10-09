package com.yeyou.phonebox

/** Windows 虚拟键码（VK）与其显示名，供「映射按键」选择器使用。 */
data class KeyOption(val code: Int, val label: String)

object KeyCodes {

    val all: List<KeyOption> = listOf(
        // 方向键
        KeyOption(37, "←（左）"),
        KeyOption(38, "↑（上）"),
        KeyOption(39, "→（右）"),
        KeyOption(40, "↓（下）"),
        // 字母
        KeyOption(65, "A"), KeyOption(66, "B"), KeyOption(67, "C"), KeyOption(68, "D"),
        KeyOption(69, "E"), KeyOption(70, "F"), KeyOption(71, "G"), KeyOption(72, "H"),
        KeyOption(73, "I"), KeyOption(74, "J"), KeyOption(75, "K"), KeyOption(76, "L"),
        KeyOption(77, "M"), KeyOption(78, "N"), KeyOption(79, "O"), KeyOption(80, "P"),
        KeyOption(81, "Q"), KeyOption(82, "R"), KeyOption(83, "S"), KeyOption(84, "T"),
        KeyOption(85, "U"), KeyOption(86, "V"), KeyOption(87, "W"), KeyOption(88, "X"),
        KeyOption(89, "Y"), KeyOption(90, "Z"),
        // 数字
        KeyOption(48, "0"), KeyOption(49, "1"), KeyOption(50, "2"), KeyOption(51, "3"),
        KeyOption(52, "4"), KeyOption(53, "5"), KeyOption(54, "6"), KeyOption(55, "7"),
        KeyOption(56, "8"), KeyOption(57, "9"),
        // 常用功能键
        KeyOption(32, "空格"),
        KeyOption(13, "回车"),
        KeyOption(27, "Esc"),
        KeyOption(9, "Tab"),
        KeyOption(8, "退格"),
        KeyOption(16, "Shift"),
        KeyOption(17, "Ctrl"),
        KeyOption(18, "Alt"),
        // F1~F12
        KeyOption(112, "F1"), KeyOption(113, "F2"), KeyOption(114, "F3"), KeyOption(115, "F4"),
        KeyOption(116, "F5"), KeyOption(117, "F6"), KeyOption(118, "F7"), KeyOption(119, "F8"),
        KeyOption(120, "F9"), KeyOption(121, "F10"), KeyOption(122, "F11"), KeyOption(123, "F12")
    )

    /** 根据 VK 码查显示名；未知时兜底显示「键(码)」。 */
    fun labelOf(code: Int): String =
        all.firstOrNull { it.code == code }?.label ?: "键($code)"
}
