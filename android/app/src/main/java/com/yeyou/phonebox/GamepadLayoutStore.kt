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
