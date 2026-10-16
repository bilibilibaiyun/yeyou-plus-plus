package com.yeyou.phonebox.web

import android.annotation.SuppressLint
import android.os.Bundle
import android.view.inputmethod.EditorInfo
import android.webkit.WebChromeClient
import android.webkit.WebSettings
import android.webkit.WebView
import android.widget.Button
import android.widget.EditText
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import com.yeyou.phonebox.AppLog
import com.yeyou.phonebox.R
import java.net.URLEncoder

/**
 * 直接玩 Flash（Ruffle 注入式浏览器）。
 *
 * Tier A：WebView 加载游戏 HTML 页，document-start 注入 ruffle.js，Ruffle polyfill
 * 自动把页面里的 Flash <embed>/<object> 替换为播放器，SWF 同源加载。
 * Tier B：输入裸 .swf 地址时，原生下载到缓存，同源喂给本地 player.html 播放（绕 CORS）。
 */
class FlashBrowserActivity : AppCompatActivity() {

    companion object {
        private const val HOME_URL = "https://www.bing.com"
        private const val MIN_WEBVIEW_MAJOR = 80
    }

    private lateinit var webView: WebView
    private lateinit var addressBox: EditText

    /** 是否处于主页（用于返回键逐级后退：网页后退 → 主页 → finish）。 */
    private var onHome = true

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_flash_browser)

        webView = findViewById(R.id.flash_webview)
        addressBox = findViewById(R.id.flash_address)

        setupWebView()
        checkWebViewVersion()

        findViewById<Button>(R.id.flash_back).setOnClickListener { goBack() }
        findViewById<Button>(R.id.flash_refresh).setOnClickListener { webView.reload() }
        findViewById<Button>(R.id.flash_favorite).setOnClickListener { showFavorites() }
        findViewById<Button>(R.id.flash_home).setOnClickListener { goHome() }

        addressBox.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_GO) {
                navigate(addressBox.text.toString())
                true
            } else {
                false
            }
        }

        goHome()
    }

    @SuppressLint("SetJavaScriptEnabled")
    private fun setupWebView() {
        val settings = webView.settings
        settings.javaScriptEnabled = true
        settings.domStorageEnabled = true
        // Ruffle 播放 Flash 声音需要自动播放，关闭用户手势要求。
        settings.mediaPlaybackRequiresUserGesture = false
        settings.cacheMode = WebSettings.LOAD_DEFAULT
        settings.allowFileAccess = false
        settings.allowContentAccess = true

        webView.webViewClient = RuffleWebViewClient(this, SwfCache.cacheDir(this))
        webView.webChromeClient = WebChromeClient()

        // 在 document-start 一次性注册注入脚本（首次导航起即生效，无需每次 onPageStarted 重复注册）。
        if (WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT)) {
            WebViewCompat.addDocumentStartJavaScript(
                webView,
                RuffleConfig.buildInjectionScript(),
                setOf("*://*/*")
            )
        } else {
            AppLog.w("FlashBrowserActivity", "当前 WebView 不支持 DOCUMENT_START_SCRIPT")
        }
    }

    private fun checkWebViewVersion() {
        val pkg = WebViewCompat.getCurrentWebViewPackage(this)
        val major = pkg?.versionName?.substringBefore('.')?.toIntOrNull() ?: 0
        if (pkg == null || major < MIN_WEBVIEW_MAJOR) {
            Toast.makeText(
                this,
                "系统 WebView 版本过旧，可能无法运行 Flash 游戏，请到应用商店更新 WebView。",
                Toast.LENGTH_LONG
            ).show()
        }
    }

    private fun navigate(input: String) {
        val url = UrlResolver.resolve(input)
        if (UrlResolver.isSwf(url)) {
            openSwf(url)
        } else {
            webView.loadUrl(url)
            onHome = false
            addressBox.setText(url)
        }
    }

    private fun openSwf(url: String) {
        Toast.makeText(this, "正在下载 SWF…", Toast.LENGTH_SHORT).show()
        Thread {
            try {
                if (SwfCache.get(this, url) == null) {
                    val bytes = SwfDownloader.download(url)
                    SwfCache.put(this, url, bytes)
                }
                val swfUrl = SwfCache.appAssetsUrl(url)
                val playerUrl = "https://appassets.androidplatform.net/player.html?swf=" +
                    URLEncoder.encode(swfUrl, "UTF-8")
                runOnUiThread {
                    webView.loadUrl(playerUrl)
                    onHome = false
                    addressBox.setText(url)
                }
            } catch (e: Exception) {
                AppLog.e("FlashBrowserActivity", "打开 SWF 失败", e)
                runOnUiThread {
                    Toast.makeText(this, "SWF 下载失败：${e.message}", Toast.LENGTH_LONG).show()
                }
            }
        }.start()
    }

    private fun goBack() {
        if (webView.canGoBack()) {
            webView.goBack()
        } else {
            goHome()
        }
    }

    private fun goHome() {
        webView.loadUrl(HOME_URL)
        onHome = true
        addressBox.setText("")
    }

    private fun showFavorites() {
        val items = FavoritesStore.load(this)
        if (items.isEmpty()) {
            Toast.makeText(this, "暂无收藏", Toast.LENGTH_SHORT).show()
            return
        }

        val labels = items.map { it.name }.toTypedArray()
        AlertDialog.Builder(this)
            .setTitle("收藏")
            .setItems(labels) { _, which ->
                navigate(items[which].url)
            }
            .setPositiveButton("收藏当前页") { _, _ -> addCurrentFavorite() }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun addCurrentFavorite() {
        val url = webView.url ?: return
        if (url.startsWith("https://appassets.androidplatform.net/")) {
            return
        }
        FavoritesStore.add(this, url, webView.title?.ifBlank { url } ?: url)
        Toast.makeText(this, "已收藏", Toast.LENGTH_SHORT).show()
    }

    override fun onBackPressed() {
        when {
            webView.canGoBack() -> webView.goBack()
            !onHome -> goHome()
            else -> super.onBackPressed() // finish
        }
    }

    override fun onDestroy() {
        webView.destroy()
        super.onDestroy()
    }
}
