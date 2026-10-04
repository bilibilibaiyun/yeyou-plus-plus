using CefSharp;
using CefSharp.Handler;
using System;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 为所有普通 http(s) 请求注入与伪装 UA（Chrome/120）一致的 User-Agent Client Hints
    /// （Sec-CH-UA / Sec-CH-UA-Platform / Sec-CH-UA-Mobile）。
    ///
    /// 背景：CefSettings.UserAgent 只覆盖 HTTP 头的 User-Agent，但内核仍是 Chromium 84。
    /// 现代人机验证服务（极验 / 腾讯防水墙 / 新版 reCAPTCHA 等）还会校验客户端提示头，
    /// 内核发出的 Sec-CH-UA 仍是 Chromium 84 或缺失，与 Chrome/120 的 UA 明显不一致，
    /// 会被判定为伪造而拒绝渲染验证码。此处通过请求拦截器把两者补齐为一致值。
    /// </summary>
    public class CaptchaRequestHandler : RequestHandler
    {
        protected override IResourceRequestHandler GetResourceRequestHandler(
            IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            bool isNavigation, bool isDownload, string requestInitiator,
            ref bool disableDefaultHandling)
        {
            // flash.cn 的联网验证请求仍交给 FlashVerifyBlocker（ResourceRequestHandlerFactory）
            // 取消，避免验证失败导致 ppapi（Flash）进程崩溃。
            // CefSharp 的 ClientAdapter 在 RequestHandler 返回 null 时才会继续查询工厂，
            // 因此这里必须返回 null，不能为 flash.cn 也注入头并抢先接管请求。
            if (request != null && (request.Url ?? string.Empty).IndexOf(
                "flash.cn", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            // 其它请求返回一个会注入客户端提示头的资源请求处理器。
            return new CaptchaResourceRequestHandler();
        }
    }

    /// <summary>
    /// 在请求发出前（CEF IO 线程）注入 Sec-CH-UA 系列客户端提示头。
    /// 值须与 Program.cs 中 Chrome/120 的 UserAgent 保持一致。
    /// </summary>
    public class CaptchaResourceRequestHandler : ResourceRequestHandler
    {
        private const string SecChUa =
            "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"";
        private const string SecChUaPlatform = "\"Windows\"";
        private const string SecChUaMobile = "?0";

        protected override CefReturnValue OnBeforeResourceLoad(
            IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, IRequestCallback callback)
        {
            // 仅对普通 http(s) 请求注入，避免影响 about:blank / devtools / data / file 等内部请求。
            if (request != null && IsHttpOrHttpsUrl(request.Url))
            {
                request.SetHeaderByName("Sec-CH-UA", SecChUa, overwrite: true);
                request.SetHeaderByName("Sec-CH-UA-Platform", SecChUaPlatform, overwrite: true);
                request.SetHeaderByName("Sec-CH-UA-Mobile", SecChUaMobile, overwrite: true);
            }

            return CefReturnValue.Continue;
        }

        private static bool IsHttpOrHttpsUrl(string url)
        {
            return !string.IsNullOrEmpty(url) &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
