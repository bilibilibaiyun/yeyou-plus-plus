using CefSharp;
using CefSharp.WinForms;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 用 HwndHost 把 WinForms 版 ChromiumWebBrowser 桥接到 WPF，
    /// 避免 CefSharp.Wpf 的 AirSpace 问题（参考 CefFlashBrowser）。
    /// </summary>
    public class BrowserHost : HwndHost
    {
        private readonly ChromiumWebBrowser browser;
        private string title;
        private string pendingUrl;
        private double pendingZoomPercent = 100;

        public event EventHandler AddressChanged;
        public event EventHandler TitleChanged;
        public event EventHandler LoadingStateChanged;

        public string Address => browser.Address;
        public string Title => title;
        public bool IsLoading => browser.IsLoading;
        public bool CanGoBack => browser.CanGoBack;
        public bool CanGoForward => browser.CanGoForward;

        public BrowserHost(string initialUrl = "about:blank", IRequestContext requestContext = null)
        {
#pragma warning disable CS0618
            // 传 null 用全局 RequestContext（普通浏览），
            // 传独立 context 则实现影子（小号）的 cookie/缓存隔离。
            browser = requestContext == null
                ? new ChromiumWebBrowser(initialUrl)
                : new ChromiumWebBrowser(initialUrl, requestContext);
            browser.CreateControl();
#pragma warning restore CS0618

            browser.AddressChanged += OnBrowserAddressChanged;
            browser.TitleChanged += OnBrowserTitleChanged;
            browser.LoadingStateChanged += OnBrowserLoadingStateChanged;
            browser.IsBrowserInitializedChanged += OnBrowserInitializedChanged;

            // 拦截重橙 Flash 的联网验证请求（api.flash.cn），避免 ppapi 进程崩溃。
            browser.ResourceRequestHandlerFactory = new FlashVerifyBlocker();

            // 下载文件保存到设置的下载目录。
            browser.DownloadHandler = new BrowserDownloadHandler();
        }

        private void OnBrowserInitializedChanged(object sender, EventArgs e)
        {
            if (!browser.IsBrowserInitialized)
            {
                return;
            }

            // 启用 Flash 插件内容（自动播放，无需用户点击允许）。
            try
            {
                var host = browser.GetBrowserHost();
                host?.RequestContext.SetPreference(
                    "profile.default_content_setting_values.plugins", 1, out _);
            }
            catch
            {
                // 忽略偏好设置失败。
            }

            // 若初始化前设置过缩放，则此刻应用。
            if (Math.Abs(pendingZoomPercent - 100) > 0.01)
            {
                var z = pendingZoomPercent;
                pendingZoomPercent = 100;
                SetZoomPercent(z);
            }

            // 若初始化前调用过 Load，则此刻真正发起导航。
            if (!string.IsNullOrEmpty(pendingUrl))
            {
                var url = pendingUrl;
                pendingUrl = null;
                browser.Load(url);
            }
        }

        private void OnBrowserAddressChanged(object sender, AddressChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => AddressChanged?.Invoke(this, EventArgs.Empty));
        }

        private void OnBrowserTitleChanged(object sender, TitleChangedEventArgs e)
        {
            title = e.Title;
            Dispatcher.InvokeAsync(() => TitleChanged?.Invoke(this, EventArgs.Empty));
        }

        private void OnBrowserLoadingStateChanged(object sender, LoadingStateChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => LoadingStateChanged?.Invoke(this, EventArgs.Empty));
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            SetParent(browser.Handle, hwndParent.Handle);
            return new HandleRef(this, browser.Handle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (!browser.IsDisposed)
            {
                browser.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !browser.IsDisposed)
            {
                browser.AddressChanged -= OnBrowserAddressChanged;
                browser.TitleChanged -= OnBrowserTitleChanged;
                browser.LoadingStateChanged -= OnBrowserLoadingStateChanged;
                browser.IsBrowserInitializedChanged -= OnBrowserInitializedChanged;
                browser.Dispose();
            }
            base.Dispose(disposing);
        }

        public void Load(string url)
        {
            if (browser.IsBrowserInitialized)
            {
                browser.Load(url);
                pendingUrl = null;
            }
            else
            {
                // 浏览器尚未初始化，暂存待初始化完成后自动加载。
                pendingUrl = url;
            }
        }

        public void Back() => browser.Back();
        public void Forward() => browser.Forward();
        public void Reload() => browser.Reload();
        public void Stop() => browser.Stop();

        /// <summary>设置页面缩放百分比（50~200）。</summary>
        public void SetZoomPercent(double percent)
        {
            if (percent < 25) percent = 25;
            if (percent > 300) percent = 300;

            if (browser.IsBrowserInitialized)
            {
                // Chromium 缩放级别公式：level = log(percent/100) / log(1.2)。
                var level = Math.Log(percent / 100.0) / Math.Log(1.2);
                browser.GetBrowser()?.SetZoomLevel(level);
            }
            else
            {
                pendingZoomPercent = percent;
            }
        }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
}

/// <summary>
/// 下载处理器：所有文件静默保存到设置的下载目录，重名自动加序号。
/// </summary>
public class BrowserDownloadHandler : IDownloadHandler
{
    public void OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser,
        DownloadItem downloadItem, IBeforeDownloadCallback callback)
    {
        if (!callback.IsDisposed)
        {
            var dir = AppPaths.DownloadDir;
            var name = string.IsNullOrWhiteSpace(downloadItem.SuggestedFileName)
                ? "download.bin"
                : downloadItem.SuggestedFileName;

            var path = Path.Combine(dir, name);
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (int i = 1; File.Exists(path); i++)
            {
                path = Path.Combine(dir, stem + "(" + i + ")" + ext);
            }

            callback.Continue(path, showDialog: false);
        }
    }

    public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser,
        DownloadItem downloadItem, IDownloadItemCallback callback)
    {
    }
}
}
