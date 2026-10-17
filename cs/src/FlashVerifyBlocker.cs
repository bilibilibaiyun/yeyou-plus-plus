using CefSharp;
using CefSharp.Handler;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 拦截重橙 Flash 的联网验证请求（api.flash.cn / www.flash.cn 等），
    /// 让它们像「网络不可达」一样被取消，避免验证失败导致 ppapi（Flash）进程崩溃。
    /// </summary>
    public class FlashVerifyBlocker : IResourceRequestHandlerFactory
    {
        public bool HasHandlers => true;

        public IResourceRequestHandler GetResourceRequestHandler(
            IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, bool isNavigation, bool isDownload,
            string requestInitiator, ref bool disableDefaultHandling)
        {
            var url = request.Url ?? string.Empty;
            if (url.Contains("flash.cn"))
            {
                // 取消 flash.cn 的验证请求（配合 GetResourceHandler 返回 null → 请求被取消）。
                disableDefaultHandling = true;
                return new CancelHandler();
            }

            // 主文档（HTML）导航：注入 Flash 兼容脚本，伪造 navigator.plugins / mimeTypes，
            // 让 swfobject 等 Flash 检测通过（解决百田·奥奇传说等「当前浏览器不支持」问题）。
            // 是否注入由 FlashCompatInjector 依据响应 MimeType 是否为 text/html 决定。
            if (isNavigation)
            {
                return new FlashCompatInjector();
            }
            return null;
        }

        private class CancelHandler : ResourceRequestHandler
        {
            protected override IResourceHandler GetResourceHandler(
                IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
            {
                return null;
            }
        }
    }
}
