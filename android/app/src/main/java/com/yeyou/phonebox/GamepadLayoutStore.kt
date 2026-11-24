package com.yeyou.phonebox

import android.content.Context
import android.content.SharedPreferences
import org.json.JSONArray
import org.json.JSONObject

/**
 * 虚拟按键布局的本地持久化（SharedPreferences + JSON）。
 * 布局在手机端维护，用户自定义后保存，下次启动自动恢复。
 */
object GamepadLayoutStore {

    private const val PREFS = "phonebox_gamepad"
    private const val KEY_LAYOUT = "layout_json"

    private const val DEFAULT_SIZE = 0.09f
    private const val DEFAULT_OPACITY = 0.45f

    /** 主机 KeyLayout 中 name → 归一化中心坐标（与桌面端 ClientKeyLayout 对齐）。 */
    private val nameToPosition: Map<String, FloatArray> = mapOf(
        "up" to floatArrayOf(0.27f, 0.50f),
        "down" to floatArrayOf(0.27f, 0.80f),
        "left" to floatArrayOf(0.18f, 0.65f),
        "right" to floatArrayOf(0.36f, 0.65f),
        "action1" to floatArrayOf(0.82f, 0.65f),
        "action2" to floatArrayOf(0.72f, 0.80f),
        "action3" to floatArrayOf(0.66f, 0.65f),
        "action4" to floatArrayOf(0.72f, 0.50f),
        "a" to floatArrayOf(0.82f, 0.65f),
        "b" to floatArrayOf(0.72f, 0.80f),
        "c" to floatArrayOf(0.66f, 0.65f),
        "d" to floatArrayOf(0.72f, 0.50f)
    )

    /** 主机 KeyLayout 中 name → 显示文字。 */
    private val nameToLabel: Map<String, String> = mapOf(
        "up" to "↑",
        "down" to "↓",
        "left" to "←",
        "right" to "→",
        "action1" to "A",
        "action2" to "B",
        "action3" to "C",
        "action4" to "D",
        "a" to "A",
        "b" to "B",
        "c" to "C",
        "d" to "D"
    )

    /** 默认布局：左侧方向键（十字）+ 右侧 A/B/C/D 动作键（菱形）。 */
    fun defaultButtons(): List<GamepadButton> = listOf(
        GamepadButton(1, "←", 37, 0.18f, 0.65f, 0.09f, 0.45f),
        GamepadButton(2, "↑", 38, 0.27f, 0.50f, 0.09f, 0.45f),
        GamepadButton(3, "↓", 40, 0.27f, 0.80f, 0.09f, 0.45f),
        GamepadButton(4, "→", 39, 0.36f, 0.65f, 0.09f, 0.45f),
        GamepadButton(5, "A", 88, 0.82f, 0.65f, 0.09f, 0.45f),
        GamepadButton(6, "B", 90, 0.72f, 0.80f, 0.09f, 0.45f),
        GamepadButton(7, "C", 67, 0.66f, 0.65f, 0.09f, 0.45f),
        GamepadButton(8, "D", 86, 0.72f, 0.50f, 0.09f, 0.45f)
    )

    /** 读取已保存布局；没有或损坏返回 null（调用方回退默认）。 */
    fun load(context: Context): List<GamepadButton>? {
        val json = prefs(context).getString(KEY_LAYOUT, null) ?: return null
        return try {
            parse(json)
        } catch (_: Exception) {
            null
        }
    }

    /** 保存布局。 */
    fun save(context: Context, buttons: List<GamepadButton>) {
        val arr = JSONArray()
        for (b in buttons) {
            arr.put(
                JSONObject()
                    .put("id", b.id)
                    .put("label", b.label)
                    .put("keyCode", b.keyCode)
                    .put("x", b.x.toDouble())
                    .put("y", b.y.toDouble())
                    .put("size", b.size.toDouble())
                    .put("opacity", b.opacity.toDouble())
            )
        }
        prefs(context).edit().putString(KEY_LAYOUT, arr.toString()).apply()
    }

    /**
     * 解析主机下发的 KeyLayout JSON（{"keys":[{"name":"up","keyCode":38},...]}）。
     * 每项可含 name/keyCode，可选 label/x/y/size/opacity。解析失败或为空返回 null（调用方回退默认）。
     */
    fun fromConfigKeyLayout(json: String): List<GamepadButton>? {
        if (json.isBlank()) {
            return null
        }
        return try {
            val obj = JSONObject(json)
            val arr = obj.optJSONArray("keys") ?: return null
            if (arr.length() == 0) {
                return null
            }

            val result = ArrayList<GamepadButton>(arr.length())
            var id = 1
            for (i in 0 until arr.length()) {
                val o = arr.optJSONObject(i) ?: continue
                val keyCode = o.optInt("keyCode", 0)
                if (keyCode <= 0) {
                    continue
                }
                val name = o.optString("name", "")
                val label = o.optString("label", "").ifBlank { labelOf(name, keyCode) }
                val pos = positionOf(name, i)
                val x = o.optDouble("x", pos[0].toDouble()).toFloat()
                val y = o.optDouble("y", pos[1].toDouble()).toFloat()
                val size = o.optDouble("size", DEFAULT_SIZE.toDouble()).toFloat()
                val opacity = o.optDouble("opacity", DEFAULT_OPACITY.toDouble()).toFloat()
                result.add(
                    GamepadButton(
                        id = id++,
                        label = label,
                        keyCode = keyCode,
                        x = x.coerceIn(0.02f, 0.98f),
                        y = y.coerceIn(0.02f, 0.98f),
                        size = size.coerceIn(0.04f, 0.30f),
                        opacity = opacity.coerceIn(0.05f, 1.0f)
                    )
                )
            }
            if (result.isEmpty()) null else result
        } catch (_: Exception) {
            null
        }
    }

    private fun positionOf(name: String, index: Int): FloatArray {
        nameToPosition[name.lowercase()]?.let { return it }
        // 未知名称按顺序在画面上部平均铺开。
        val x = if (index <= 0) 0.5f else (0.18f + 0.64f * ((index - 1) % 4) / 3f)
        val y = if (index <= 0) 0.65f else 0.35f
        return floatArrayOf(x, y)
    }

    private fun labelOf(name: String, keyCode: Int): String {
        nameToLabel[name.lowercase()]?.let { return it }
        if (name.isNotBlank()) {
            return name
        }
        return KeyCodes.labelOf(keyCode)
    }

    private fun parse(json: String): List<GamepadButton> {
        val arr = JSONArray(json)
        val result = ArrayList<GamepadButton>(arr.length())
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            result.add(
                GamepadButton(
                    id = o.getInt("id"),
                    label = o.getString("label"),
                    keyCode = o.getInt("keyCode"),
                    x = o.getDouble("x").toFloat(),
                    y = o.getDouble("y").toFloat(),
                    size = o.getDouble("size").toFloat(),
                    opacity = o.getDouble("opacity").toFloat()
                )
            )
        }
        return result
    }

    private fun prefs(context: Context): SharedPreferences =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
