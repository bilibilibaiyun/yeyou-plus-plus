using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 浏览器扩展管理：扫描扩展目录（每个子目录一个解压后的扩展），
    /// 生成 CEF --load-extension 启动参数，提供商店跳转。
    /// </summary>
    public static class ExtensionsManager
    {
        /// <summary>扩展商店地址。</summary>
        public const string EdgeStoreUrl = "https://microsoftedge.microsoft.com/addons";
        public const string ChromeStoreUrl = "https://chrome.google.com/webstore";

        /// <summary>扩展安装目录（每个扩展一个子目录，含 manifest.json）。</summary>
        public static string ExtensionsDir
        {
            get
            {
                var dir = Path.Combine(AppPaths.DataDir, "extensions");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        /// <summary>已安装的扩展（目录名 + manifest 里的名称/版本）。</summary>
        public class ExtensionInfo
        {
            public string Folder { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
        }

        /// <summary>扫描扩展目录，返回含 manifest.json 的扩展。</summary>
        public static List<ExtensionInfo> GetInstalled()
        {
            var result = new List<ExtensionInfo>();
            try
            {
                foreach (var dir in Directory.GetDirectories(ExtensionsDir))
                {
                    var manifest = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifest))
                    {
                        continue;
                    }
                    string name = Path.GetFileName(dir);
                    string version = string.Empty;
                    try
                    {
                        var json = JObject.Parse(File.ReadAllText(manifest));
                        name = json.Value<string>("name") ?? name;
                        version = json.Value<string>("version") ?? string.Empty;
                    }
                    catch
                    {
                        // manifest 解析失败就用目录名。
                    }
                    result.Add(new ExtensionInfo
                    {
                        Folder = dir,
                        Name = name,
                        Version = version
                    });
                }
            }
            catch
            {
                // 目录不可读则返回空。
            }
            return result;
        }

        /// <summary>生成 CEF --load-extension 参数值（逗号分隔多个目录）；无扩展返回空。</summary>
        public static string BuildLoadExtensionArg()
        {
            var folders = new List<string>();
            foreach (var ext in GetInstalled())
            {
                // 路径含逗号会让 --load-extension 解析错乱，跳过这种目录。
                if (ext.Folder != null && !ext.Folder.Contains(","))
                {
                    folders.Add(ext.Folder);
                }
            }
            return folders.Count > 0 ? string.Join(",", folders) : string.Empty;
        }

        /// <summary>在资源管理器中打开扩展目录。</summary>
        public static void OpenFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ExtensionsDir,
                    UseShellExecute = true
                });
            }
            catch
            {
                // 忽略打开失败。
            }
        }

        /// <summary>打开扩展商店网页。</summary>
        public static void OpenStore(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // 忽略打开失败。
            }
        }
    }
}
