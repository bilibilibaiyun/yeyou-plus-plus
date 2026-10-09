package com.yeyou.phonebox

import org.json.JSONArray
import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.URL

/**
 * 手机盒子内置更新检查器（与电脑端一致，分「检测更新」与「选择版本」两个入口）。
 *
 * 通过 GitHub Releases API 获取所有带 APK 资产的 release：
 *  - 检测更新：取最新（版本号最大）的一条，版本号大于当前即提示更新。
 *  - 选择版本：列出全部版本（含测试版），用户自选后下载安装。
 *
 * APK 资产名约定：`YeyouPlusPlus-PhoneBox_vX.Y.apk`，版本号即 `v` 后的数字段。
 */
object UpdateChecker {

    private const val REPO_API =
        "https://api.github.com/repos/bilibilibaiyun/yeyou-plus-plus/releases"

    private val APK_NAME_REGEX =
        Regex("PhoneBox_v([0-9]+(?:\\.[0-9]+)*)\\.apk", RegexOption.IGNORE_CASE)

    /** 一个可安装的版本（对应某个 release 里的 APK 资产）。 */
    data class ReleaseItem(
        val tag: String,
        val apkVersion: String,
        val downloadUrl: String,
        val prerelease: Boolean
    )

    /** 列出所有带 APK 的版本，按版本号降序（最新在前）。 */
    fun listReleases(): List<ReleaseItem> {
        val releases = fetchReleases()
        val items = ArrayList<ReleaseItem>()
        for (r in 0 until releases.length()) {
            val release = releases.getJSONObject(r)
            val tag = release.optString("tag_name", "")
            val prerelease = release.optBoolean("prerelease", false)
            val assets = release.optJSONArray("assets") ?: continue
            for (i in 0 until assets.length()) {
                val asset = assets.getJSONObject(i)
                val name = asset.optString("name", "")
                val match = APK_NAME_REGEX.find(name) ?: continue
                val apkVersion = match.groupValues[1]
                val url = asset.optString("browser_download_url", "")
                if (url.isNotEmpty()) {
                    items.add(ReleaseItem(tag, apkVersion, url, prerelease))
                }
            }
        }
        return items.sortedWith { a, b -> -compareVersion(a.apkVersion, b.apkVersion) }
    }

    /** 检测更新：返回最新版本（版本号 > currentVersion 时），无更新返回 null。 */
    fun check(currentVersion: String): ReleaseItem? {
        val latest = listReleases().firstOrNull() ?: return null
        return if (compareVersion(latest.apkVersion, currentVersion) > 0) latest else null
    }

    /** 下载 APK 到目标文件，回调进度（0..100）。 */
    fun download(url: String, dest: File, onProgress: ((Int) -> Unit)? = null) {
        val conn = URL(url).openConnection() as HttpURLConnection
        conn.connectTimeout = 15000
        conn.readTimeout = 60000
        conn.setRequestProperty("User-Agent", "YeyouPlusPlus-PhoneBox")
        try {
            val code = conn.responseCode
            if (code !in 200..299) {
                throw Exception("下载失败：HTTP $code")
            }
            val total = conn.contentLengthLong
            val input = conn.inputStream
            val output = FileOutputStream(dest)
            val buf = ByteArray(8192)
            var read: Int
            var downloaded = 0L
            while (input.read(buf).also { read = it } != -1) {
                output.write(buf, 0, read)
                downloaded += read
                if (total > 0) {
                    onProgress?.invoke(((downloaded * 100) / total).toInt())
                }
            }
            output.flush()
            output.close()
            input.close()
        } finally {
            conn.disconnect()
        }
    }

    /** 按点分段的版本号比较：a>b 返回正数，a<b 返回负数，相等返回 0。 */
    fun compareVersion(a: String, b: String): Int {
        val pa = a.split('.').map { it.trim().toIntOrNull() ?: 0 }
        val pb = b.split('.').map { it.trim().toIntOrNull() ?: 0 }
        val n = maxOf(pa.size, pb.size)
        for (i in 0 until n) {
            val x = pa.getOrElse(i) { 0 }
            val y = pb.getOrElse(i) { 0 }
            if (x != y) return x.compareTo(y)
        }
        return 0
    }

    private fun fetchReleases(): JSONArray {
        val conn = URL(REPO_API).openConnection() as HttpURLConnection
        conn.connectTimeout = 10000
        conn.readTimeout = 10000
        conn.setRequestProperty("Accept", "application/vnd.github+json")
        conn.setRequestProperty("User-Agent", "YeyouPlusPlus-PhoneBox")
        try {
            val code = conn.responseCode
            if (code !in 200..299) {
                throw Exception("HTTP $code")
            }
            return JSONArray(conn.inputStream.bufferedReader().readText())
        } finally {
            conn.disconnect()
        }
    }
}
