using CefSharp;
using CefSharp.Handler;
using System;
using System.IO;
using System.Text;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 风险接口（e.4399.cn/risk）响应日志记录器（诊断兜底）。
    ///
    /// 仅在请求 URL 命中 e.4399.cn/risk 时接管请求，记录 HTTP 状态码与响应体到
    /// %TEMP%\risk-debug.log；其它请求一律返回 null 交给默认网络加载器，零额外开销。
    ///
    /// 注意：CefSharp 的 ClientAdapter 在 RequestHandler 返回 null 时会继续查询
    /// ResourceRequestHandlerFactory，因此 flash.cn 的验证请求仍会走 FlashVerifyBlocker
    /// 被取消，行为与之前完全一致。
    /// </summary>
    public class RiskLogRequestHandler : RequestHandler
    {
        protected override IResourceRequestHandler GetResourceRequestHandler(
            IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            bool isNavigation, bool isDownload, string requestInitiator,
            ref bool disableDefaultHandling)
        {
            var url = request?.Url ?? string.Empty;
            if (url.IndexOf("e.4399.cn/risk", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new RiskResourceRequestHandler(url);
            }

            // 其它请求不接管，返回 null 让默认处理（含 flash.cn → FlashVerifyBlocker）。
            return null;
        }
    }

    /// <summary>
    /// 记录 risk 接口的响应状态码，并为响应体安装透传过滤器以抓取内容。
    /// </summary>
    public class RiskResourceRequestHandler : ResourceRequestHandler
    {
        private readonly string url;

        public RiskResourceRequestHandler(string url)
        {
            this.url = url;
        }

        /// <summary>
        /// 响应头到达时记录「URL + HTTP 状态码」。
        /// 返回 false 表示不做任何修改，让资源加载继续。
        /// </summary>
        protected override bool OnResourceResponse(
            IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, IResponse response)
        {
            var targetUrl = request?.Url ?? url;
            var statusCode = response?.StatusCode ?? 0;

            RiskLog.Append(
                "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " +
                targetUrl + " HTTP " + statusCode + " ====" + Environment.NewLine);

            return false;
        }

        /// <summary>
        /// 为响应体安装透传过滤器：既把原始字节原样写回 dataOut（保证页面/插件正常收到响应），
        /// 又在内存中累积一份，待响应结束时追加写入日志。
        /// </summary>
        protected override IResponseFilter GetResourceResponseFilter(
            IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, IResponse response)
        {
            return new RiskResponseFilter();
        }
    }

    /// <summary>
    /// 透传响应过滤器：把输入字节原样写到输出，同时累积到内存；响应结束时解码为 UTF-8 写入日志。
    ///
    /// CefSharp 84 的 IResponseFilter 只有 InitFilter + Filter（没有原生 CEF 的 ProcessInput）。
    /// 当 CEF 以 dataIn == null 再调用一次 Filter 时表示响应流结束，此时落盘；
    /// Dispose 作为兜底再 flush 一次，并用标志位避免重复写入。
    /// </summary>
    public class RiskResponseFilter : IResponseFilter
    {
        // 防御性上限：只抓取前 1MB，避免异常大响应撑爆内存（risk 接口返回的是小 JSON）。
        private const int MaxCaptureBytes = 1024 * 1024;

        private readonly MemoryStream body = new MemoryStream();
        private bool flushed;

        public bool InitFilter()
        {
            return true;
        }

        public FilterStatus Filter(Stream dataIn, out long dataInRead, Stream dataOut, out long dataOutWritten)
        {
            if (dataIn == null)
            {
                // 响应流结束：CEF 以空输入再调用一次用于 flush。
                dataInRead = 0;
                dataOutWritten = 0;
                Flush();
                return FilterStatus.Done;
            }

            // 参考 CefSharp 官方 StreamResponseFilter 的透传写法：
            // 每次 Filter 拿到的是全新缓冲区，dataIn.Length 即本次可读字节数；
            // 输出缓冲区可能更小，读多少写多少，未读完时返回 NeedMoreData 继续。
            dataInRead = Math.Min(dataIn.Length, dataOut.Length);
            dataOutWritten = dataInRead;

            var buffer = new byte[dataInRead];
            dataIn.Read(buffer, 0, buffer.Length);
            dataOut.Write(buffer, 0, buffer.Length);

            if (body.Length < MaxCaptureBytes)
            {
                var remaining = MaxCaptureBytes - (int)body.Length;
                var copyCount = (int)Math.Min(buffer.Length, remaining);
                body.Write(buffer, 0, copyCount);
            }

            return dataInRead < dataIn.Length ? FilterStatus.NeedMoreData : FilterStatus.Done;
        }

        public void Dispose()
        {
            Flush();
            body.Dispose();
        }

        private void Flush()
        {
            if (flushed)
            {
                return;
            }
            flushed = true;

            try
            {
                RiskLog.Append(Encoding.UTF8.GetString(body.ToArray()) + Environment.NewLine);
            }
            catch
            {
                // 日志失败不影响页面加载。
            }
        }
    }

    /// <summary>
    /// 追加写 %TEMP%\risk-debug.log 的轻量日志工具（静态锁串行化，避免多请求互相踩）。
    /// </summary>
    public static class RiskLog
    {
        private static readonly object Sync = new object();

        public static string LogPath => Path.Combine(Path.GetTempPath(), "risk-debug.log");

        public static void Append(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                lock (Sync)
                {
                    var logPath = LogPath;

                    // 超过 2MB 时用本次内容覆盖重写，避免日志无限增长，只保留最近记录。
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 2 * 1024 * 1024)
                    {
                        File.WriteAllText(logPath, text);
                    }
                    else
                    {
                        File.AppendAllText(logPath, text);
                    }
                }
            }
            catch
            {
                // 日志失败不影响页面加载。
            }
        }
    }
}
