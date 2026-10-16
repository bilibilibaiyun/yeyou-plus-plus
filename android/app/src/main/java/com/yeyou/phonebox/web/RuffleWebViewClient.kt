package com.yeyou.phonebox.web

import android.content.Context
import android.net.Uri
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.webkit.WebViewAssetLoader
import com.yeyou.phonebox.AppLog
import java.io.File
import java.io.FileInputStream

/**
 * Ruffle 浏览器 WebViewClient。
 *
 * 用 WebViewAssetLoader 把本地资源映射到虚拟域名：
 *   - /ruffle/ → assets/ruffle/（ruffle.js、core.*.js、*.wasm）
 *   - /swf/    → filesDir/swf/（下载缓存后的裸 SWF，Tier B 同源喂给 player.html）
 *   - /        → assets/（player.html）
 *
 * 其中 .wasm 显式返回 application/wasm，否则 WebView 按 octet-stream 处理会导致
 * WebAssembly 实例化失败。
 */
class RuffleWebViewClient(
    context: Context,
    private val swfCacheDir: File
) : WebViewClient() {

    private val appContext: Context = context.applicationContext

    // 单一根路径处理器：把 appassets 根路径映射到 assets/ 根目录，
    // 覆盖 ruffle/ruffle.js、ruffle/core.*.js、ruffle/*.wasm 与 player.html。
    private val assetLoader: WebViewAssetLoader = WebViewAssetLoader.Builder()
        .addPathHandler("/", WebViewAssetLoader.AssetsPathHandler(appContext))
        .build()

    override fun shouldInterceptRequest(
        view: WebView,
        request: WebResourceRequest
    ): WebResourceResponse? {
        val url = request.url
        val urlString = url.toString()

        if (url.host == RuffleConfig.ASSET_HOST) {
            // .wasm 显式指定 MIME，否则 WebView 按 application/octet-stream 处理导致 wasm 实例化失败。
            if (urlString.endsWith(".wasm", ignoreCase = true)) {
                return interceptWasm(url)
            }
            // 本地 SWF 缓存（Tier B：player.html 里的 embed 经此同源加载）。
            if (url.path?.startsWith(RuffleConfig.SWF_PATH) == true) {
                return interceptSwf(url)
            }
        }

        return assetLoader.shouldInterceptRequest(url)
    }

    private fun interceptWasm(url: Uri): WebResourceResponse? {
        val path = url.path ?: return null
        val fileName = path.substringAfterLast('/')
        if (!fileName.endsWith(".wasm", ignoreCase = true)) {
            return null
        }

        return try {
            val stream = appContext.assets.open("ruffle/$fileName")
            WebResourceResponse("application/wasm", "utf-8", stream)
        } catch (e: Exception) {
            AppLog.w("RuffleWebViewClient", "打开 wasm 失败：${e.message}")
            null
        }
    }

    private fun interceptSwf(url: Uri): WebResourceResponse? {
        val fileName = url.path?.substringAfterLast('/') ?: return null
        if (fileName.isBlank()) {
            return null
        }

        val file = File(swfCacheDir, fileName)
        if (!file.exists()) {
            return null
        }

        return try {
            WebResourceResponse("application/x-shockwave-flash", "utf-8", FileInputStream(file))
        } catch (e: Exception) {
            AppLog.w("RuffleWebViewClient", "打开 SWF 缓存失败：${e.message}")
            null
        }
    }
}
