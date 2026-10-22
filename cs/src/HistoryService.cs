using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YeyouPlusPlus
{
    /// <summary>一条浏览历史记录。</summary>
    public class HistoryItem
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public DateTime LastVisitedUtc { get; set; }
    }

    /// <summary>
    /// 浏览历史：内存列表 + JSON 单文件持久化（history.json）。
    /// CefSharp 84 无历史查询 API，因此在浏览器导航事件里自捕获。
    /// 按规范化 URL 合并（保留 scheme/query，去 fragment），90 天保留 + 5000 条上限兜底。
    /// </summary>
    public static class HistoryService
    {
        public const int RetentionDays = 90;
        public const int MaxItems = 5000;

        private static List<HistoryItem> _items;

        static HistoryService()
        {
            _items = Load();
            Prune();
        }

        private static string FilePath
        {
            get { return AppPaths.HistoryFilePath; }
        }

        /// <summary>全部历史记录（按最近访问倒序）。</summary>
        public static IReadOnlyList<HistoryItem> Items
        {
            get { return _items.OrderByDescending(i => i.LastVisitedUtc).ToList(); }
        }

        /// <summary>数据目录变更后重新加载。</summary>
        public static void Reload()
        {
            _items = Load();
            Prune();
            Save();
        }

        /// <summary>
        /// 记录一次访问：合并同 URL（更新最近访问时间与标题）或新插入，随后淘汰过期/超量条目并落盘。
        /// 非 http/https 或空 URL 直接忽略。
        /// </summary>
        public static void Record(string url, string title)
        {
            var key = NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var item = _items.FirstOrDefault(i => i.Url == key);
            var now = DateTime.UtcNow;
            if (item == null)
            {
                item = new HistoryItem { Url = key };
                _items.Add(item);
            }
            item.LastVisitedUtc = now;
            if (!string.IsNullOrWhiteSpace(title))
            {
                item.Title = title.Trim();
            }
            else if (string.IsNullOrWhiteSpace(item.Title))
            {
                item.Title = key;
            }

            Prune();
            Save();
        }

        /// <summary>用较晚到达的页面标题刷新同 URL 最近记录的标题。</summary>
        public static void UpdateTitle(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }
            var key = NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var item = _items.Where(i => i.Url == key)
                .OrderByDescending(i => i.LastVisitedUtc)
                .FirstOrDefault();
            if (item != null && item.Title != title)
            {
                item.Title = title.Trim();
                Save();
            }
        }

        /// <summary>删除某 URL 的历史记录。</summary>
        public static void Remove(string url)
        {
            var key = NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            _items.RemoveAll(i => i.Url == key);
            Save();
        }

        /// <summary>清空全部历史记录。</summary>
        public static void Clear()
        {
            _items.Clear();
            Save();
        }

        /// <summary>按关键词搜索（URL + 标题忽略大小写包含匹配），返回最近访问倒序列表。</summary>
        public static List<HistoryItem> Search(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Items.ToList();
            }
            var kw = keyword.Trim();
            return _items
                .Where(i => i.Url.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (i.Title ?? string.Empty).IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(i => i.LastVisitedUtc)
                .ToList();
        }

        /// <summary>
        /// 规范化历史 URL：仅接受 http/https，去 fragment、保留 scheme/query，host 小写。
        /// 无法解析时返回空串。
        /// </summary>
        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            try
            {
                var uri = new Uri(url.Trim());
                // SchemeAndServer 含 scheme://host[:port]，PathAndQuery 含 /path?query，均不含 fragment。
                return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.UriEscaped);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>淘汰：先删早于 90 天的，再按最旧优先删到 MaxItems 以内。</summary>
        private static void Prune()
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            _items.RemoveAll(i => i.LastVisitedUtc < cutoff);

            if (_items.Count > MaxItems)
            {
                var toRemove = _items
                    .OrderBy(i => i.LastVisitedUtc)
                    .Take(_items.Count - MaxItems)
                    .ToList();
                foreach (var item in toRemove)
                {
                    _items.Remove(item);
                }
            }
        }

        private static List<HistoryItem> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonConvert.DeserializeObject<List<HistoryItem>>(json)
                        ?? new List<HistoryItem>();
                    return list.Where(i => !string.IsNullOrWhiteSpace(i.Url)).ToList();
                }
            }
            catch
            {
                // 损坏则用空列表。
            }
            return new List<HistoryItem>();
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
