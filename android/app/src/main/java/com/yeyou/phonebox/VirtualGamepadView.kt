package com.yeyou.phonebox

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View

/**
 * 可高度自定义的虚拟按键悬浮层。
 *
 * 两种模式：
 *  - 游玩模式（默认）：按下/松开回调，半透明悬浮在画面之上。
 *  - 编辑模式（editMode=true）：单击选中、拖动移动；大小/透明度/映射按键由外层面板控制。
 *
 * 每个按键的「位置/大小/透明度/映射键」都可自定义，布局经 [GamepadLayoutStore] 持久化。
 */
class VirtualGamepadView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    interface Listener {
        fun onButtonDown(button: GamepadButton)
        fun onButtonUp(button: GamepadButton)
        fun onButtonSelected(button: GamepadButton?)
        fun onLayoutChanged(buttons: List<GamepadButton>)
    }

    var listener: Listener? = null

    /** 当前按键布局。 */
    var buttons: List<GamepadButton> = GamepadLayoutStore.defaultButtons()
        private set

    /** 是否处于编辑模式。 */
    var editMode: Boolean = false
        set(value) {
            if (field != value) {
                field = value
                selectedId = -1
                listener?.onButtonSelected(null)
                invalidate()
            }
        }

    /** 当前选中的按键 id（编辑模式用）。 */
    var selectedId: Int = -1
        private set

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val strokePaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val selPaint = Paint(Paint.ANTI_ALIAS_FLAG)

    // 游玩模式多点触控状态
    private val pointerToButton = HashMap<Int, Int>()
    private val buttonPressCount = HashMap<Int, Int>()

    // 编辑模式拖拽状态
    private var draggingId = -1
    private var dragPointerId = -1
    private var dragLastX = 0f
    private var dragLastY = 0f

    init {
        strokePaint.style = Paint.Style.STROKE
        strokePaint.color = Color.argb(200, 255, 255, 255)
        strokePaint.strokeWidth = dp(2f)

        textPaint.color = Color.WHITE
        textPaint.textAlign = Paint.Align.CENTER
        textPaint.isFakeBoldText = true

        selPaint.style = Paint.Style.STROKE
        selPaint.color = Color.argb(255, 0, 200, 255)
        selPaint.strokeWidth = dp(3f)
    }

    fun setButtons(newButtons: List<GamepadButton>) {
        buttons = newButtons.toList()
        invalidate()
    }

    /** 外部选中指定按键（编辑模式用）。传 -1 表示取消选中。 */
    fun select(id: Int) {
        selectedId = id
        invalidate()
    }

    /** 更新某个按键（保留 id），并触发 onLayoutChanged。 */
    fun updateButton(updated: GamepadButton) {
        buttons = buttons.map { if (it.id == updated.id) updated else it }
        listener?.onLayoutChanged(buttons)
        invalidate()
    }

    // ---- 几何 ----

    private fun rectFor(b: GamepadButton): RectF {
        val r = b.size * minOf(width, height).toFloat()
        return RectF(b.x * width - r, b.y * height - r, b.x * width + r, b.y * height + r)
    }

    /** 命中检测：返回按钮下标（后绘制的优先）。 */
    private fun hitTest(x: Float, y: Float): Int? {
        for (i in buttons.indices.reversed()) {
            if (rectFor(buttons[i]).contains(x, y)) {
                return i
            }
        }
        return null
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        for ((index, b) in buttons.withIndex()) {
            val rect = rectFor(b)
            val pressed = (buttonPressCount[index] ?: 0) > 0
            val alpha = (b.opacity.coerceIn(0.05f, 1f) * 255).toInt()

            fillPaint.color = if (pressed) {
                Color.argb(alpha, 76, 175, 80)
            } else {
                Color.argb(alpha, 255, 255, 255)
            }
            canvas.drawOval(rect, fillPaint)
            canvas.drawOval(rect, strokePaint)

            if (editMode && b.id == selectedId) {
                canvas.drawOval(rect, selPaint)
            }

            val textSize = b.size * minOf(width, height) * 0.9f
            textPaint.textSize = textSize
            textPaint.alpha = alpha
            val baseline = rect.centerY() - (textPaint.ascent() + textPaint.descent()) / 2f
            canvas.drawText(b.label, rect.centerX(), baseline, textPaint)
        }
        textPaint.alpha = 255
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        return if (editMode) handleEditTouch(event) else handlePlayTouch(event)
    }

    // ---- 游玩模式 ----

    private fun handlePlayTouch(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN,
            MotionEvent.ACTION_POINTER_DOWN -> {
                val idx = event.actionIndex
                handleDown(event.getPointerId(idx), event.getX(idx), event.getY(idx))
                return true
            }
            MotionEvent.ACTION_MOVE -> return true
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

    // ---- 编辑模式：单击选中 + 拖动移动 ----

    private fun handleEditTouch(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                val x = event.getX()
                val y = event.getY()
                val hit = hitTest(x, y)
                if (hit != null) {
                    selectedId = buttons[hit].id
                    listener?.onButtonSelected(buttons[hit])
                    draggingId = selectedId
                    dragPointerId = event.getPointerId(0)
                    dragLastX = x
                    dragLastY = y
                } else {
                    selectedId = -1
                    listener?.onButtonSelected(null)
                }
                invalidate()
                return true
            }
            MotionEvent.ACTION_MOVE -> {
                if (draggingId < 0) {
                    return true
                }
                val idx = event.findPointerIndex(dragPointerId)
                if (idx >= 0) {
                    val x = event.getX(idx)
                    val y = event.getY(idx)
                    val b = buttons.firstOrNull { it.id == draggingId } ?: return true
                    val nx = (b.x * width + (x - dragLastX)) / width
                    val ny = (b.y * height + (y - dragLastY)) / height
                    updateButton(
                        b.copy(
                            x = nx.coerceIn(0.02f, 0.98f),
                            y = ny.coerceIn(0.02f, 0.98f)
                        )
                    )
                    dragLastX = x
                    dragLastY = y
                }
                return true
            }
            MotionEvent.ACTION_UP,
            MotionEvent.ACTION_CANCEL -> {
                draggingId = -1
                dragPointerId = -1
                return true
            }
        }
        return super.onTouchEvent(event)
    }

    private fun dp(value: Float): Float = value * context.resources.displayMetrics.density
}
