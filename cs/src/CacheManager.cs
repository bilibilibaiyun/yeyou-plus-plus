using System;
using System.IO;
using System.Threading.Tasks;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 缓存目录管理：统计大小 + 清理（删 cookies + 删除缓存目录，锁定的文件忽略）。
    /// </summary>
    public static class CacheManager
    {
        /// <summary>
        /// 递归计算目录总大小（字节）。目录不存在返回 0，无法访问的项忽略。
        /// </summary>
        public static long GetDirectorySize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return 0L;
            }

            long total = 0L;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // 跳过无法访问的文件。
                    }
                }
            }
            catch
            {
                // 枚举中途失败（如权限不足），返回已累计的大小。
            }

            return total;
        }

        /// <summary>
        /// 清理 CEF 缓存：删除 cookies + 删除缓存目录。被 CEF 锁定的文件跳过不报错。
        /// 目录删除在后台线程执行，完成后通过 <paramref name="onCompleted"/> 回调到调用线程（UI 线程）。
        /// </summary>
        public static void ClearCache(Action onCompleted = null)
        {
            try
            {
                var context = CefSharp.Cef.GetGlobalRequestContext();
                context.GetCookieManager(null)?.DeleteCookies("", "", null);
            }
            catch
            {
                // 忽略 Cookie 清理失败。
            }

            // 大缓存目录的删除可能耗时数秒，放到后台线程避免卡 UI。
            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(AppPaths.CacheDir))
                    {
                        Directory.Delete(AppPaths.CacheDir, true);
                    }
                }
                catch
                {
                    // 部分文件可能被 CEF 锁定，忽略。
                }
            }).ContinueWith(_ =>
            {
                onCompleted?.Invoke();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
