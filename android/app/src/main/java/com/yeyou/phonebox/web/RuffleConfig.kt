package com.yeyou.phonebox.web

/**
 * Ruffle（Flash 模拟器）集成配置与 document-start 注入脚本。
 *
 * Ruffle 自托管资源位于 assets/ruffle/，通过 WebViewAssetLoader 映射到虚拟域名
 * https://appassets.androidplatform.net/ 下；publicPath 指向该目录，Ruffle 内部据此
 * 加载 core.ruffle.*.js 与 *.wasm。
 */
object RuffleConfig {

    /** WebViewAssetLoader 虚拟域名主机。 */
    const val ASSET_HOST = "appassets.androidplatform.net"

    /** 虚拟域名基础地址。 */
    const val ASSET_BASE = "https://appassets.androidplatform.net"

    /** Ruffle 资源目录（publicPath）。 */
    const val RUFFLE_PUBLIC_PATH = "https://appassets.androidplatform.net/ruffle/"

    /** 本地 player.html 相对路径。 */
    const val PLAYER_PATH = "/player.html"

    /** 本地 SWF 缓存虚拟路径前缀。 */
    const val SWF_PATH = "/swf/"

    /**
     * 构建注入到远程页面的脚本：设置 Ruffle 配置并动态加载 ruffle.js。
     *
     * 本地 player.html 已自带 ruffle.js 加载，因此这里对 appassets 域名直接跳过，避免重复加载。
     */
    fun buildInjectionScript(): String = """
        (function () {
            if (window.location && window.location.hostname === "appassets.androidplatform.net") {
                return;
            }
            window.RufflePlayer = window.RufflePlayer || {};
            window.RufflePlayer.config = {
                autoplay: "on",
                letterbox: "on",
                scale: "showAll",
                publicPath: "https://appassets.androidplatform.net/ruffle/"
            };
            function loadRuffle() {
                var s = document.createElement("script");
                s.src = "https://appassets.androidplatform.net/ruffle/ruffle.js";
                s.async = false;
                (document.head || document.documentElement).appendChild(s);
            }
            if (document.documentElement || document.head) {
                loadRuffle();
            } else {
                document.addEventListener("DOMContentLoaded", loadRuffle, { once: true });
            }
        })();
    """.trimIndent()
}
