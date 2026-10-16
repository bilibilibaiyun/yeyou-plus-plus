package com.yeyou.phonebox.web

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/**
 * 裸 SWF 下载器（HttpURLConnection）。
 */
object SwfDownloader {

    private const val CONNECT_TIMEOUT_MS = 15_000
    private const val READ_TIMEOUT_MS = 30_000

    /** 下载 URL 指向的文件，非 2xx 抛异常。 */
    fun download(url: String): ByteArray {
        val connection = URL(url).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = CONNECT_TIMEOUT_MS
            connection.readTimeout = READ_TIMEOUT_MS
            connection.instanceFollowRedirects = true
            connection.requestMethod = "GET"
            connection.setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android) YeyouPlusPlus/1.0")
            connection.connect()

            val code = connection.responseCode
            if (code !in 200..299) {
                throw IOException("下载 SWF 失败：HTTP $code")
            }

            return connection.inputStream.use { it.readBytes() }
        } finally {
            connection.disconnect()
        }
    }
}
