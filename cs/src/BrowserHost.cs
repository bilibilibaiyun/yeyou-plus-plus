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
    public class BrowserHost : HwndHost, ILifeSpanHandler
    {
        private readonly ChromiumWebBrowser browser;
        private string title;
        private string pendingUrl;
        private double pendingZoomPercent = 100;

        public event EventHandler AddressChanged;
        public event EventHandler TitleChanged;
        public event EventHandler LoadingStateChanged;
        /// <summary>网页请求在新窗口（target=_blank / window.open）打开时触发，参数为目标 URL。</summary>
        public event Action<string> PopupRequested;

        public string Address => browser.Address;
        public string Title => title;
        public bool IsLoading => browser.IsLoading;
        public bool CanGoBack => browser.CanGoBack;
        public bool CanGoForward => browser.CanGoForward;
        /// <summary>[诊断] 浏览器是否已完成初始化。</summary>
        public bool IsBrowserInitializedForDiag => browser.IsBrowserInitialized;

        public BrowserHost(string initialUrl = "about:blank", IRequestContext requestContext = null)
        {
#pragma warning disable CS0618
            // 传 null 用全局 RequestContext（普通浏览），
            // 传独立 context 则实现影子的 cookie/缓存隔离。
            browser = requestContext == null
                ? new ChromiumWebBrowser(initialUrl)
                : new ChromiumWebBrowser(initialUrl, requestContext);
            browser.CreateControl();
#pragma warning restore CS0618

            // 拦截 target=_blank / window.open：取消 CEF 默认弹窗，改走 PopupRequested 在新标签打开。
            browser.LifeSpanHandler = this;

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
            // 注意：OnAfterCreated 时机主 frame 可能尚未就绪，立即 Load 会被静默吞掉
            //（影子标签/新建标签空白的根因），延迟到 Dispatcher 下一拍再导航。
            if (!string.IsNullOrEmpty(pendingUrl))
            {
                var url = pendingUrl;
                pendingUrl = null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!browser.IsDisposed)
                    {
                        browser.Load(url);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
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

        // ---- ILifeSpanHandler：把新窗口请求转成新标签页打开 ----

        /// <summary>网页请求创建弹窗（target=_blank / window.open）时回调。</summary>
        public bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition,
            bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo,
            IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null;

            // 只有 target=_blank 之类的新标签请求才转成新标签页打开，
            // 并取消 CEF 默认的新窗口创建。
            if (targetDisposition == WindowOpenDisposition.NewForegroundTab ||
                targetDisposition == WindowOpenDisposition.NewBackgroundTab)
            {
                // 仅对普通 http(s) 链接转新标签；about:blank / javascript: / 空串等
                // 特殊 URL 直接取消即可，避免 OpenInTab 的 ResolveUrl 处理出错。
                if (IsHttpOrHttpsUrl(targetUrl))
                {
                    PopupRequested?.Invoke(targetUrl);
                }
                return true;
            }

            // QQ/微信扫码登录等依赖 window.open 的 OAuth 授权窗口（NewPopup/NewWindow），
            // 以及 Unknown / CurrentTab / SaveToDisk 等其它 disposition，一律放行，
            // 交给 CEF 默认行为创建真正的弹窗窗口，否则会因 window.opener 引用断裂导致闪退。
            return false;
        }

        /// <summary>判断 URL 是否为普通的 http/https 链接。</summary>
        private static bool IsHttpOrHttpsUrl(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        public void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
        }

        public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
            return false;
        }

        public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
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
