using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 缓存目录管理：只统计/清理白名单缓存子目录，绝不触碰 Cookies / Local Storage / IndexedDB
    /// （保登录态与游戏存档）。CefSharp 84 无 ClearCache API，故采用文件系统逐删 + 跳过锁定。
    /// </summary>
    public static class CacheManager
    {
        /// <summary>
        /// 可安全删除的 CEF 缓存子目录白名单（与 DataBackup.SkipDirectories 对齐）。
        /// 注意：CacheDir（默认 profile）之外的影子 profile 缓存不在自动清理范围内。
        /// </summary>
        public static IReadOnlyList<string> CacheSubDirNames { get; } = new List<string>
        {
            "Cache",
            "Code Cache",
            "GPUCache",
            "ShaderCache",
            "GrShaderCache",
            "DawnGraphiteCache",
            "DawnWebGPUCache"
        };

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

        /// <summary>只统计白名单缓存子目录内的文件大小（与清理范围一致）。</summary>
        public static long GetCacheSize()
        {
            long total = 0L;
            foreach (var name in CacheSubDirNames)
            {
                total += GetDirectorySize(Path.Combine(AppPaths.CacheDir, name));
            }
            return total;
        }

        /// <summary>
        /// 后台流式清理缓存：按 LastWriteTimeUtc 最旧优先逐删，累计剩余大小 ≤ targetBytes 即停。
        /// 被 CEF 锁定的文件（IOException / UnauthorizedAccessException）跳过。完成后经
        /// <paramref name="onCompleted"/> 回调到调用线程（UI 线程）。
        /// </summary>
        public static void CleanToTarget(long targetBytes, Action onCompleted = null)
        {
            Task.Run(() =>
            {
                var cacheDir = AppPaths.CacheDir;
                var files = new List<Tuple<string, long, DateTime>>();

                foreach (var name in CacheSubDirNames)
                {
                    var dir = Path.Combine(cacheDir, name);
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var fi = new FileInfo(file);
                                files.Add(Tuple.Create(file, fi.Length, fi.LastWriteTimeUtc));
                            }
                            catch
                            {
                                // 跳过无法读取元数据的文件。
                            }
                        }
                    }
                    catch
                    {
                        // 枚举失败跳过该子目录。
                    }
                }

                long remaining = 0L;
                foreach (var f in files)
                {
                    remaining += f.Item2;
                }

                foreach (var f in files.OrderBy(f => f.Item3))
                {
                    if (remaining <= targetBytes)
                    {
                        break;
                    }
                    try
                    {
                        File.Delete(f.Item1);
                        remaining -= f.Item2;
                    }
                    catch (IOException)
                    {
                        // CEF 锁定，跳过。
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 无权限，跳过。
                    }
                    catch
                    {
                        // 其它异常同样跳过，不中断清理。
                    }
                }

                // 尝试删除已清空的缓存子目录（可选，失败忽略）。
                foreach (var name in CacheSubDirNames)
                {
                    var dir = Path.Combine(cacheDir, name);
                    try
                    {
                        if (Directory.Exists(dir) &&
                            !Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            Directory.Delete(dir);
                        }
                    }
                    catch
                    {
                        // 忽略目录删除失败。
                    }
                }
            }).ContinueWith(_ =>
            {
                onCompleted?.Invoke();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// 手动「清理缓存」：仅清缓存、不删 cookies（保留登录状态）。
        /// 底层改为逐删实现（清到 0），修复 Directory.Delete(CacheDir, true) 删不动的 bug。
        /// </summary>
        public static void ClearCache(Action onCompleted = null)
        {
            CleanToTarget(0L, onCompleted);
        }
    }
}
