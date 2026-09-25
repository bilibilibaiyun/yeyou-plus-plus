using CefSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YeyouPlusPlus
{
    /// <summary>一个「影子」：同一网站的一套独立 cookie / 缓存，可长期保存。</summary>
    public class ShadowItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public string Url { get; set; }
        public string CreatedAt { get; set; }
    }

    /// <summary>
    /// 影子管理：每个影子对应一个独立的 CEF RequestContext（独立缓存目录），
    /// 从而实现 cookie / 缓存 / localStorage 与原浏览完全隔离，且长期持久化。
    /// </summary>
    public static class ShadowManager
    {
        private static List<ShadowItem> _items;
        private static readonly Dictionary<string, IRequestContext> _contexts =
            new Dictionary<string, IRequestContext>();
        private static readonly object Lock = new object();

        private static string FilePath
        {
            get { return Path.Combine(AppPaths.DataDir, "shadows.json"); }
        }

        static ShadowManager()
        {
            _items = Load();
        }

        public static IReadOnlyList<ShadowItem> Items
        {
            get { return _items; }
        }

        /// <summary>某网站（host）下的所有影子。</summary>
        public static List<ShadowItem> GetForHost(string hostOrUrl)
        {
            var host = NormalizeHost(hostOrUrl);
            return _items.Where(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>判断某网站是否已创建过影子。</summary>
        public static bool HasShadowForHost(string hostOrUrl)
        {
            return GetForHost(hostOrUrl).Count > 0;
        }

        /// <summary>添加一个影子，返回新影子。</summary>
        public static ShadowItem Add(string name, string hostOrUrl, string url)
        {
            var item = new ShadowItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Name = string.IsNullOrWhiteSpace(name) ? "影子" : name.Trim(),
                Host = NormalizeHost(hostOrUrl),
                Url = url,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            _items.Add(item);
            Save();
            return item;
        }

        /// <summary>删除影子（元数据 + 缓存目录）。</summary>
        public static void Remove(string id)
        {
            _items.RemoveAll(x => x.Id == id);
            Save();
            // 缓存目录异步删除（可能被 CEF 锁定，忽略失败）。
            var dir = Path.Combine(AppPaths.ProfilesRoot, "shadow_" + id);
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // 锁定文件忽略。
            }
        }

        /// <summary>获取（或创建）某影子的独立 RequestContext，同一影子复用同一 context。</summary>
        public static IRequestContext GetContext(string id)
        {
            lock (Lock)
            {
                IRequestContext ctx;
                if (_contexts.TryGetValue(id, out ctx))
                {
                    return ctx;
                }
                var cachePath = Path.Combine(AppPaths.ProfilesRoot, "shadow_" + id);
                try { Directory.CreateDirectory(cachePath); } catch { }
                var settings = new RequestContextSettings { CachePath = cachePath };
                ctx = new RequestContext(settings);
                _contexts[id] = ctx;
                return ctx;
            }
        }

        /// <summary>数据目录变更后重新加载元数据。</summary>
        public static void Reload()
        {
            _items = Load();
        }

        private static string NormalizeHost(string hostOrUrl)
        {
            if (string.IsNullOrWhiteSpace(hostOrUrl))
            {
                return string.Empty;
            }
            try
            {
                var raw = hostOrUrl.Contains("://") ? hostOrUrl : "http://" + hostOrUrl;
                return new Uri(raw).Host.Trim().ToLowerInvariant();
            }
            catch
            {
                return hostOrUrl.Trim().ToLowerInvariant();
            }
        }

        private static List<ShadowItem> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonConvert.DeserializeObject<List<ShadowItem>>(File.ReadAllText(FilePath))
                        ?? new List<ShadowItem>();
                }
            }
            catch
            {
                // 损坏则用空列表。
            }
            return new List<ShadowItem>();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(_items, Formatting.Indented));
            }
            catch
            {
                // 忽略保存失败。
            }
        }
    }
}
