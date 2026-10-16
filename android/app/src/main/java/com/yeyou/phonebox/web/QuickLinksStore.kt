package com.yeyou.phonebox.web

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** 快捷入口项（与桌面端 QuickLinks.QuickLinkItem 对齐：name + url）。 */
data class QuickLinkItem(val name: String, val url: String)

/**
 * 主页快捷入口数据层：filesDir/quicklinks.json，固定 5 槽位，schema [{name,url}]。
 */
object QuickLinksStore {

    const val SLOT_COUNT = 5
    private const val FILE_NAME = "quicklinks.json"

    fun load(context: Context): List<QuickLinkItem?> {
        val slots = arrayOfNulls<QuickLinkItem>(SLOT_COUNT)
        val file = File(context.filesDir, FILE_NAME)
        if (!file.exists()) {
            return slots.toList()
        }
        return try {
            val array = JSONArray(file.readText())
            for (i in 0 until minOf(array.length(), SLOT_COUNT)) {
                val obj = array.optJSONObject(i) ?: continue
                val url = obj.optString("url", "").trim()
                if (url.isEmpty()) {
                    continue
                }
                slots[i] = QuickLinkItem(obj.optString("name", "").trim(), url)
            }
            slots.toList()
        } catch (e: Exception) {
            slots.toList()
        }
    }

    fun get(context: Context, index: Int): QuickLinkItem? =
        if (index in 0 until SLOT_COUNT) load(context)[index] else null

    fun set(context: Context, index: Int, url: String, name: String) {
        if (index !in 0 until SLOT_COUNT || url.isBlank()) {
            return
        }
        val slots = load(context).toMutableList()
        while (slots.size < SLOT_COUNT) {
            slots.add(null)
        }
        slots[index] = QuickLinkItem(name.trim(), url.trim())
        save(context, slots)
    }

    fun remove(context: Context, index: Int) {
        if (index !in 0 until SLOT_COUNT) {
            return
        }
        val slots = load(context).toMutableList()
        while (slots.size < SLOT_COUNT) {
            slots.add(null)
        }
        slots[index] = null
        save(context, slots)
    }

    /** 展示文字：名称优先，其次域名，空槽位「+」。 */
    fun displayName(item: QuickLinkItem?): String {
        if (item == null || item.url.isBlank()) {
            return "+"
        }
        return item.name.ifBlank { hostOf(item.url).ifBlank { item.url } }
    }

    private fun hostOf(url: String): String = try {
        val raw = if (url.contains("://")) url else "http://$url"
        java.net.URI(raw).host?.removePrefix("www.") ?: ""
    } catch (e: Exception) {
        ""
    }

    private fun save(context: Context, slots: List<QuickLinkItem?>) {
        try {
            val array = JSONArray()
            for (i in 0 until SLOT_COUNT) {
                val item = slots.getOrNull(i)
                if (item == null) {
                    array.put(JSONObject.NULL)
                } else {
                    array.put(JSONObject().put("name", item.name).put("url", item.url))
                }
            }
            File(context.filesDir, FILE_NAME).writeText(array.toString())
        } catch (e: Exception) {
            // 忽略保存失败。
        }
    }
}
