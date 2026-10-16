package com.yeyou.phonebox.web

import android.content.Context
import java.io.File
import java.security.MessageDigest

/**
 * 裸 SWF 本地缓存：按 URL 哈希落盘到 filesDir/swf/，限制总大小，超限时清最旧文件。
 */
object SwfCache {

    private const val DIR_NAME = "swf"
    private const val MAX_TOTAL_BYTES = 64L * 1024 * 1024 // 64 MB

    fun cacheDir(context: Context): File =
        File(context.filesDir, DIR_NAME).apply { mkdirs() }

    fun fileNameFor(url: String): String = sha1(url) + ".swf"

    fun fileFor(context: Context, url: String): File =
        File(cacheDir(context), fileNameFor(url))

    /** 命中缓存返回文件，否则返回 null。 */
    fun get(context: Context, url: String): File? {
        val file = fileFor(context, url)
        return if (file.exists() && file.length() > 0) file else null
    }

    /** 写入缓存并返回文件（写后触发容量清理）。 */
    fun put(context: Context, url: String, bytes: ByteArray): File {
        val file = fileFor(context, url)
        file.parentFile?.mkdirs()
        file.writeBytes(bytes)
        evictIfNeeded(context)
        return file
    }

    /** 缓存文件对应的虚拟域名 URL（供 player.html 同源加载）。 */
    fun appAssetsUrl(url: String): String =
        "https://appassets.androidplatform.net/swf/" + fileNameFor(url)

    private fun evictIfNeeded(context: Context) {
        val dir = cacheDir(context)
        val files = dir.listFiles()?.toList()?.sortedBy { it.lastModified() } ?: return
        var total = files.sumOf { it.length() }
        var index = 0
        while (total > MAX_TOTAL_BYTES && index < files.size - 1) {
            val file = files[index]
            total -= file.length()
            file.delete()
            index++
        }
    }

    private fun sha1(input: String): String {
        val digest = MessageDigest.getInstance("SHA-1").digest(input.toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it) }
    }
}
