package com.yeyou.phonebox

/**
 * 单个可自定义的虚拟按键。
 *
 * @param id      唯一标识（本机内自增）
 * @param label   按键上显示的文字
 * @param keyCode Windows 虚拟键码（Virtual-Key Code），按下时经控制通道发给电脑
 * @param x       归一化中心横坐标（0..1，相对屏幕宽）
 * @param y       归一化中心纵坐标（0..1，相对屏幕高）
 * @param size    半径占屏幕短边的比例（约 0.04..0.3）
 * @param opacity 透明度 0.05..1（越大越不透明）
 */
data class GamepadButton(
    val id: Int,
    val label: String,
    val keyCode: Int,
    val x: Float,
    val y: Float,
    val size: Float,
    val opacity: Float
)
