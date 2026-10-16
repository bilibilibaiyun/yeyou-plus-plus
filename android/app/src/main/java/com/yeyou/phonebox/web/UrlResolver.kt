package com.yeyou.phonebox.web

import java.net.URLEncoder

/**
 * 地址栏 / 搜索框归一化（对齐桌面端 MainWindow.ResolveUrl）：
 * 网址原样（无协议补 http://），关键词转必应搜索。
 */
object UrlResolver {

    fun resolve(input: String): String {
        val s = input.trim()
        if (isUrl(s)) {
            return if (s.contains("://")) s else "http://$s"
        }
        val query = URLEncoder.encode(s, "UTF-8").replace("+", "%20")
        return "https://www.bing.com/search?q=$query"
    }

    /** 是否为裸 .swf 地址（去掉 query / fragment 后判断）。 */
    fun isSwf(url: String): Boolean {
        val path = url.substringBefore('#').substringBefore('?').lowercase()
        return path.endsWith(".swf")
    }

    private fun isUrl(s: String): Boolean =
        s.contains(".") && !s.contains(" ") && !s.contains("\u3000")
}
