package com.yeyou.phonebox

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View
import org.json.JSONObject

/**
 * 虚拟按键悬浮层。
 *
 * 默认布局：左侧方向键（上下左右十字）+ 右侧 A/B/C/D 动作键（菱形排列）。
 * 支持多点触控：每个 pointer 独立跟踪，按下/松开回调，按下时视觉高亮。
 * 半透明悬浮在 SurfaceView 之上，不影响画面显示。
 */
class VirtualGamepadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    /** 单个虚拟按键。 */
    data class GamepadButton(
        val name: String,
        val label: String,
        val keyCode: Int
    )

    interface Listener {
        fun onButtonDown(button: GamepadButton)
        fun onButtonUp(button: GamepadButton)
    }

    companion object {
        /**
         * 默认布局：
         * 方向键 → Windows VK：左 37 / 上 38 / 下 40 / 右 39
         * 动作键 A/B/C/D → Windows VK：A=88(X)、B=90(Z)、C=67(C)、D=86(V)
         */
        fun defaultButtons(): List<GamepadButton> = listOf(
            GamepadButton("left", "←", 37),
            GamepadButton("up", "↑", 38),
            GamepadButton("down", "↓", 40),
            GamepadButton("right", "→", 39),
            GamepadButton("A", "A", 88),
            GamepadButton("B", "B", 90),
            GamepadButton("C", "C", 67),
            GamepadButton("D", "D", 86)
        )
    }

    var listener: Listener? = null

    private var buttons: List<GamepadButton> = defaultButtons()

    /** button 下标 → 屏幕区域。 */
    private val buttonRects = HashMap<Int, RectF>()

    /** pointerId → 按下的 button 下标。 */
    private val pointerToButton = HashMap<Int, Int>()

    /** button 下标 → 被多少个 pointer 按下。 */
    private val buttonPressCount = HashMap<Int, Int>()

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val strokePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG)

    init {
        strokePaint.style = Paint.Style.STROKE
        strokePaint.color = Color.argb(200, 255, 255, 255)
        strokePaint.strokeWidth = dp(2f)

        textPaint.color = Color.WHITE
        textPaint.textAlign = Paint.Align.CENTER
        textPaint.isFakeBoldText = true
    }

    fun setButtons(newButtons: List<GamepadButton>) {
        buttons = newButtons.toList()
        buttonRects.clear()
        invalidate()
    }

    /**
     * 应用电脑端下发的按键布局（Config.KeyLayout 的 JSON 字符串）。
     * 仅同步方向键的虚拟键码；其余沿用本地默认布局。解析失败静默忽略。
     */
    fun applyKeyLayout(json: String?) {
        if (json.isNullOrBlank()) {
            return
        }
        try {
            val root = JSONObject(json)
            val keys = root.optJSONArray("keys") ?: return
            val directionCodes = HashMap<String, Int>()
            for (i in 0 until keys.length()) {
                val item = keys.optJSONObject(i) ?: continue
                val name = item.optString("name", "")
                val code = item.optInt("keyCode", 0)
                if (code > 0 && name in setOf("up", "down", "left", "right")) {
                    directionCodes[name] = code
                }
            }
            if (directionCodes.isEmpty()) {
                return
            }
            val updated = buttons.map { button ->
                directionCodes[button.name]?.let { code -> button.copy(keyCode = code) } ?: button
            }
            setButtons(updated)
        } catch (_: Exception) {
            // 布局解析失败时保持默认布局。
        }
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        layoutButtons(w, h)
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (buttonRects.isEmpty() && width > 0 && height > 0) {
            layoutButtons(width, height)
        }

        buttons.forEachIndexed { index, button ->
            val rect = buttonRects[index] ?: return@forEachIndexed
            val pressed = (buttonPressCount[index] ?: 0) > 0

            // 按下高亮：绿色半透明；未按下：白色低透明。
            fillPaint.color = if (pressed) {
                Color.argb(170, 76, 175, 80)
            } else {
                Color.argb(70, 255, 255, 255)
            }

            canvas.drawOval(rect, fillPaint)
            canvas.drawOval(rect, strokePaint)

            val baseline = rect.centerY() - (textPaint.ascent() + textPaint.descent()) / 2f
            canvas.drawText(button.label, rect.centerX(), baseline, textPaint)
        }
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (buttonRects.isEmpty()) {
            return false
        }

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN,
            MotionEvent.ACTION_POINTER_DOWN -> {
                val pointerIndex = event.actionIndex
                handleDown(
                    pointerId = event.getPointerId(pointerIndex),
                    x = event.getX(pointerIndex),
                    y = event.getY(pointerIndex)
                )
                return true
            }
            MotionEvent.ACTION_MOVE -> {
                // 本阶段不处理「手指跨按键拖动」，只跟踪按下/松开。
                return true
            }
            MotionEvent.ACTION_UP,
            MotionEvent.ACTION_POINTER_UP -> {
                handleUp(event.getPointerId(event.actionIndex))
                return true
            }
            MotionEvent.ACTION_CANCEL -> {
                pointerToButton.keys.toList().forEach { handleUp(it) }
                return true
            }
        }
        return super.onTouchEvent(event)
    }

    private fun handleDown(pointerId: Int, x: Float, y: Float) {
        if (pointerToButton.containsKey(pointerId)) {
            return
        }
        val index = hitTest(x, y) ?: return
        pointerToButton[pointerId] = index

        val count = (buttonPressCount[index] ?: 0) + 1
        buttonPressCount[index] = count
        if (count == 1) {
            buttons.getOrNull(index)?.let { listener?.onButtonDown(it) }
        }
        invalidate()
    }

    private fun handleUp(pointerId: Int) {
        val index = pointerToButton.remove(pointerId) ?: return
        val count = (buttonPressCount[index] ?: 1) - 1
        if (count <= 0) {
            buttonPressCount.remove(index)
            buttons.getOrNull(index)?.let { listener?.onButtonUp(it) }
        } else {
            buttonPressCount[index] = count
        }
        invalidate()
    }

    private fun hitTest(x: Float, y: Float): Int? {
        buttonRects.forEach { (index, rect) ->
            if (rect.contains(x, y)) {
                return index
            }
        }
        return null
    }

    /**
     * 布局计算：
     *  - 左侧方向键十字排列，中心 (w*0.25, h*0.6)
     *  - 右侧动作键菱形排列，中心 (w*0.78, h*0.6)
     * 按键半径按屏幕短边缩放，保证小屏可用。
     */
    private fun layoutButtons(w: Int, h: Int) {
        buttonRects.clear()
        if (w <= 0 || h <= 0 || buttons.isEmpty()) {
            return
        }

        val radius = minOf(w, h) * 0.11f
        val gap = radius * 0.25f
        val step = radius * 2f + gap

        val dpadCx = w * 0.25f
        val dpadCy = h * 0.60f
        val actionCx = w * 0.78f
        val actionCy = h * 0.60f

        val centers = HashMap<String, FloatArray>()
        centers["left"] = floatArrayOf(dpadCx - step, dpadCy)
        centers["up"] = floatArrayOf(dpadCx, dpadCy - step)
        centers["down"] = floatArrayOf(dpadCx, dpadCy + step)
        centers["right"] = floatArrayOf(dpadCx + step, dpadCy)

        // 动作键：A 右 / B 下 / C 左 / D 上（菱形，仿游戏手柄 ABXY 布局）。
        centers["A"] = floatArrayOf(actionCx + step, actionCy)
        centers["B"] = floatArrayOf(actionCx, actionCy + step)
        centers["C"] = floatArrayOf(actionCx - step, actionCy)
        centers["D"] = floatArrayOf(actionCx, actionCy - step)

        buttons.forEachIndexed { index, button ->
            val center = centers[button.name] ?: return@forEachIndexed
            val cx = center[0]
            val cy = center[1]
            buttonRects[index] = RectF(cx - radius, cy - radius, cx + radius, cy + radius)
        }

        textPaint.textSize = radius * 0.9f
    }

    private fun dp(value: Float): Float = value * context.resources.displayMetrics.density
}
