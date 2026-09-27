using System;
using System.Collections.Generic;
using System.IO;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 覆盖安装（升级）前的数据备份与升级后的恢复。
    /// 背景：旧版安装脚本在升级时会删除整个安装目录（含 data 目录与自定义数据目录），
    /// 导致网页账号(Cookie)、收藏、配置全部丢失。因此在运行安装包前备份数据，
    /// 新版本首次启动时（CEF 初始化前）恢复。
    /// </summary>
    public static class DataBackup
    {
        /// <summary>
        /// 备份时需要跳过的目录名（忽略大小写）。
        /// 这些目录体积大、可随时重建，且部分文件在 CEF 运行期间被锁定，
        /// 备份它们既无必要又会显著拖慢备份、阻塞 UI。
        /// </summary>
        private static readonly HashSet<string> SkipDirectories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "update",
                "downloads",
                "Cache",
                "Code Cache",
                "GPUCache",
                "ShaderCache",
                "GrShaderCache",
                "DawnGraphiteCache",
                "DawnWebGPUCache"
            };

        /// <summary>备份根目录：优先安装盘根目录，回退系统临时目录。</summary>
        private static string BackupRoot
        {
            get
            {
                var root = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
                var candidates = new[]
                {
                    Path.Combine(root ?? "D:\\", "YeyouPlusPlus_data_backup"),
                    Path.Combine(Path.GetTempPath(), "YeyouPlusPlus_data_backup")
                };
                foreach (var c in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(c);
                        return c;
                    }
                    catch
                    {
                        // 尝试下一个候选。
                    }
                }
                return null;
            }
        }

        /// <summary>覆盖安装前备份数据目录。返回是否成功。</summary>
        public static bool Backup()
        {
            var bak = BackupRoot;
            if (bak == null)
            {
                return false;
            }
            try
            {
                if (Directory.Exists(bak))
                {
                    Directory.Delete(bak, true);
                }
                Directory.CreateDirectory(bak);

                var defaultDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                var customDir = AppPaths.DataDir; // 可能等于 defaultDir

                // 1) 备份默认数据目录（含 settings.json 配置文件）。
                if (Directory.Exists(defaultDir))
                {
                    CopyDir(defaultDir, Path.Combine(bak, "default"));
                }

                // 2) 若用户自定义了数据目录，额外备份自定义目录。
                if (!PathsEqual(customDir, defaultDir) && Directory.Exists(customDir))
                {
                    CopyDir(customDir, Path.Combine(bak, "custom"));
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>程序启动时恢复备份数据（若存在）。必须在 CEF 初始化之前调用。</summary>
        public static void RestoreIfNeeded()
        {
            var bak = BackupRoot;
            if (bak == null || !Directory.Exists(bak))
            {
                return;
            }
            try
            {
                var defaultDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

                // 1) 恢复默认数据目录。
                var bakDefault = Path.Combine(bak, "default");
                if (Directory.Exists(bakDefault))
                {
                    Directory.CreateDirectory(defaultDir);
                    foreach (var entry in Directory.GetFileSystemEntries(bakDefault))
                    {
                        CopyEntry(entry, Path.Combine(defaultDir, Path.GetFileName(entry)));
                    }
                }

                // 2) 从恢复后的 settings.json 读出自定义数据目录，恢复自定义数据。
                string customTarget = ReadCustomDataDir(defaultDir);
                var bakCustom = Path.Combine(bak, "custom");
                if (Directory.Exists(bakCustom) && !string.IsNullOrEmpty(customTarget))
                {
                    Directory.CreateDirectory(customTarget);
                    foreach (var entry in Directory.GetFileSystemEntries(bakCustom))
                    {
                        CopyEntry(entry, Path.Combine(customTarget, Path.GetFileName(entry)));
                    }
                }

                // 3) 清理备份。
                Directory.Delete(bak, true);
            }
            catch
            {
                // 恢复失败不阻塞启动。
            }
        }

        private static string ReadCustomDataDir(string defaultDir)
        {
            var settings = Path.Combine(defaultDir, "settings.json");
            if (!File.Exists(settings))
            {
                return null;
            }
            try
            {
                var s = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(settings));
                return string.IsNullOrWhiteSpace(s?.DataDirPath) ? null : s.DataDirPath.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\', '/'),
                    Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CopyEntry(string src, string dst)
        {
            if (Directory.Exists(src))
            {
                CopyDir(src, dst);
            }
            else if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                try
                {
                    File.Copy(src, dst, true);
                }
                catch
                {
                    // 单个文件可能被 CEF 短暂锁定，忽略该文件，继续复制其余数据。
                }
            }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                try
                {
                    File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
                }
                catch
                {
                    // 单个文件可能被 CEF 短暂锁定，忽略该文件，继续复制其余数据。
                }
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                var name = Path.GetFileName(d);
                if (SkipDirectories.Contains(name))
                {
                    continue;
                }
                CopyDir(d, Path.Combine(dst, name));
            }
        }
    }
}
