package com.yeyou.phonebox.web

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/** 收藏项（与桌面端 Favorites.FavoriteItem 对齐：name + url）。 */
data class FavoriteItem(val name: String, val url: String)

/**
 * 收藏夹数据层：filesDir/favorites.json，schema [{name,url}]。
 */
object FavoritesStore {

    private const val FILE_NAME = "favorites.json"

    fun load(context: Context): List<FavoriteItem> {
        val file = File(context.filesDir, FILE_NAME)
        if (!file.exists()) {
            return emptyList()
        }
        return try {
            val array = JSONArray(file.readText())
            val list = mutableListOf<FavoriteItem>()
            for (i in 0 until array.length()) {
                val obj = array.optJSONObject(i) ?: continue
                val url = obj.optString("url", "").trim()
                if (url.isEmpty()) {
                    continue
                }
                val name = obj.optString("name", "").ifBlank { url }
                list.add(FavoriteItem(name, url))
            }
            list
        } catch (e: Exception) {
            emptyList()
        }
    }

    fun contains(context: Context, url: String): Boolean =
        load(context).any { it.url == url }

    fun add(context: Context, url: String, name: String) {
        if (url.isBlank()) {
            return
        }
        val items = load(context).toMutableList()
        if (items.any { it.url == url }) {
            return
        }
        items.add(0, FavoriteItem(name.ifBlank { url }, url))
        save(context, items)
    }

    fun remove(context: Context, url: String) {
        save(context, load(context).filter { it.url != url })
    }

    private fun save(context: Context, items: List<FavoriteItem>) {
        try {
            val array = JSONArray()
            for (item in items) {
                array.put(JSONObject().put("name", item.name).put("url", item.url))
            }
            File(context.filesDir, FILE_NAME).writeText(array.toString())
        } catch (e: Exception) {
            // 忽略保存失败。
        }
    }
}
