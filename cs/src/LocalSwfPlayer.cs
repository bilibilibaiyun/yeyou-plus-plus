using System;
using System.IO;
using System.Text;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 本地 swf 播放辅助：为本地 .swf 生成一个包装 HTML（&lt;embed&gt; 指向 swf），
    /// 通过 file:// 加载包装页，交给 Pepper Flash 渲染。
    ///
    /// 相比直接导航到 .swf 文件，embed 包装页是网页 Flash 游戏的标准加载方式，
    /// 不依赖 Chromium 对「顶层插件文档」的处理，兼容性更稳。
    /// </summary>
    public static class LocalSwfPlayer
    {
        /// <summary>把本地绝对路径转成 file:/// URI（自动转义中文 / 空格等字符）。</summary>
        public static string ToFileUri(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return new Uri(Path.GetFullPath(path)).AbsoluteUri;
            }
            catch
            {
                // 路径本身非法时仍尽力返回一个 URI，避免二次抛异常掩盖真实错误。
                return new Uri(path, UriKind.Absolute).AbsoluteUri;
            }
        }

        /// <summary>
        /// 为指定 swf 生成包装页并写入临时目录，返回包装页的 file:/// URI。
        /// 文件不存在 / 无权限 / 路径非法时抛出异常，由调用方负责友好提示。
        /// </summary>
        public static string CreateWrapper(string swfPath)
        {
            if (string.IsNullOrWhiteSpace(swfPath))
            {
                throw new ArgumentException("文件路径为空", nameof(swfPath));
            }

            var fullPath = Path.GetFullPath(swfPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到文件：" + fullPath, fullPath);
            }

            var swfUri = ToFileUri(fullPath);
            var fileName = Path.GetFileName(fullPath);
            var html = BuildHtml(fileName, swfUri);

            var dir = Path.Combine(Path.GetTempPath(), "YeyouPlusPlus", "swf");
            Directory.CreateDirectory(dir);
            var wrapperPath = Path.Combine(dir, "swf_" + Guid.NewGuid().ToString("N") + ".html");
            File.WriteAllText(wrapperPath, html, new UTF8Encoding(false));

            return ToFileUri(wrapperPath);
        }

        private static string BuildHtml(string title, string swfUri)
        {
            var safeTitle = EscapeHtml(title);
            var safeUri = EscapeHtmlAttribute(swfUri);

            return "<!DOCTYPE html>\n" +
                   "<html><head><meta charset=\"utf-8\">\n" +
                   "<title>" + safeTitle + "</title>\n" +
                   "<style>html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:#000;}" +
                   "embed{display:block;width:100%;height:100%;}</style>\n" +
                   "</head><body>\n" +
                   "<embed src=\"" + safeUri + "\" type=\"application/x-shockwave-flash\" " +
                   "width=\"100%\" height=\"100%\" allowFullScreen=\"true\" allowScriptAccess=\"sameDomain\" " +
                   "wmode=\"opaque\" quality=\"high\" bgcolor=\"#000000\">\n" +
                   "</body></html>";
        }

        /// <summary>
        /// 删除临时目录下所有旧的 swf_*.html 包装页。
        /// 仅在应用启动时调用一次（此时没有已打开标签，最安全）；
        /// 不要在每次 CreateWrapper 前调用，否则会误删「标签仍打开」的旧包装页，
        /// 导致切回旧 swf 标签刷新时报白屏/错误。
        /// </summary>
        public static void CleanupOldWrappers()
        {
            var dir = Path.Combine(Path.GetTempPath(), "YeyouPlusPlus", "swf");
            try
            {
                if (!Directory.Exists(dir))
                {
                    return;
                }
                foreach (var file in Directory.GetFiles(dir, "swf_*.html"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 个别文件被占用时跳过。
                    }
                }
            }
            catch
            {
                // 目录枚举失败不影响启动。
            }
        }

        private static string EscapeHtml(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string EscapeHtmlAttribute(string s)
        {
            return EscapeHtml(s).Replace("\"", "&quot;");
        }
    }
}
