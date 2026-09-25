using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 网页缩放记忆：按域名记住用户设置的缩放百分比，
    /// 下次访问同一网站自动恢复。持久化到 DataDir\zoom.json。
    /// </summary>
    public static class ZoomStore
    {
        private static readonly Dictionary<string, double> map;

        static ZoomStore()
        {
            map = Load();
        }

        private static string FilePath
        {
            get { return Path.Combine(AppPaths.DataDir, "zoom.json"); }
        }

        private static string Normalize(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return string.Empty;
            }
            try
            {
                var raw = host.Contains("://") ? host : "http://" + host;
                var h = new Uri(raw).Host;
                return h.Trim().ToLowerInvariant();
            }
            catch
            {
                return host.Trim().ToLowerInvariant();
            }
        }

        /// <summary>取某域名的缩放百分比（无记录返回 100）。</summary>
        public static double Get(string hostOrUrl)
        {
            var key = Normalize(hostOrUrl);
            if (key.Length > 0 && map.TryGetValue(key, out double v) && v >= 25 && v <= 300)
            {
                return v;
            }
            return 100;
        }

        /// <summary>记录某域名的缩放百分比（100 表示清除记录）。</summary>
        public static void Set(string hostOrUrl, double percent)
        {
            var key = Normalize(hostOrUrl);
            if (key.Length == 0)
            {
                return;
            }
            if (Math.Abs(percent - 100) < 0.5)
            {
                if (map.Remove(key))
                {
                    Save();
                }
                return;
            }
            map[key] = Math.Round(percent, 1);
            Save();
        }

        private static Dictionary<string, double> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(FilePath))
                        ?? new Dictionary<string, double>();
                }
            }
            catch
            {
                // 损坏则用空表。
            }
            return new Dictionary<string, double>();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(map, Formatting.Indented));
            }
            catch
            {
                // 忽略保存失败。
            }
        }
    }
}
