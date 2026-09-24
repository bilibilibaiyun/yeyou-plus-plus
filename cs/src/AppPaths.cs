using System;
using System.IO;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 应用路径配置。数据目录 / 下载目录支持用户自定义（设置里更改）。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>程序目录（exe 所在）。</summary>
        public static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>数据存储目录（收藏/快捷入口/图标等），可在设置中自定义。</summary>
        public static string DataDir
        {
            get
            {
                var custom = AppSettingsStore.Current.DataDirPath;
                var dir = string.IsNullOrWhiteSpace(custom)
                    ? Path.Combine(BaseDir, "data")
                    : custom.Trim();
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                    // 自定义路径不可用时回退到程序目录。
                    dir = Path.Combine(BaseDir, "data");
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        /// <summary>下载保存目录，可在设置中自定义。</summary>
        public static string DownloadDir
        {
            get
            {
                var custom = AppSettingsStore.Current.DownloadDirPath;
                var dir = string.IsNullOrWhiteSpace(custom)
                    ? Path.Combine(DataDir, "downloads")
                    : custom.Trim();
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                    dir = Path.Combine(DataDir, "downloads");
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        /// <summary>快捷入口网站图标缓存目录。</summary>
        public static string IconsDir
        {
            get
            {
                var dir = Path.Combine(DataDir, "icons");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>CEF 缓存目录。</summary>
        public static string CacheDir => Path.Combine(BaseDir, "cache");
    }
}
