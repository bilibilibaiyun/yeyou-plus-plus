using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 快捷入口单项：名称可选，为空时展示域名或 URL；IconPath 为已下载的站点图标路径。
    /// </summary>
    public class QuickLinkItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string IconPath { get; set; }
    }

    /// <summary>
    /// 主页快捷入口：固定 5 个槽位，内存列表 + JSON 持久化。
    /// 保存后自动在后台抓取站点图标（favicon）。
    /// </summary>
    public static class QuickLinks
    {
        /// <summary>快捷入口槽位数量。</summary>
        public const int SlotCount = 5;

        private static readonly List<QuickLinkItem> slots;

        /// <summary>站点图标下载完成（供 UI 刷新按钮）。</summary>
        public static event Action IconUpdated;

        static QuickLinks()
        {
            slots = Load();
        }

        private static string FilePath
        {
            get { return Path.Combine(AppPaths.DataDir, "quicklinks.json"); }
        }

        /// <summary>数据目录变更后重新加载。</summary>
        public static void Reload()
        {
            var fresh = Load();
            slots.Clear();
            slots.AddRange(fresh);
        }

        /// <summary>获取指定槽位内容，空槽位返回 null。</summary>
        public static QuickLinkItem Get(int index)
        {
            return index >= 0 && index < slots.Count ? slots[index] : null;
        }

        /// <summary>保存/覆盖指定槽位。url 为空则忽略。</summary>
        public static void Set(int index, string url, string name)
        {
            if (index < 0 || index >= SlotCount || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            EnsureSize();
            slots[index] = new QuickLinkItem
            {
                Url = url.Trim(),
                Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim()
            };
            Save();
            FetchIconAsync(index);
        }

        /// <summary>删除指定槽位（置空）。</summary>
        public static void Remove(int index)
        {
            if (index < 0 || index >= SlotCount)
            {
                return;
            }

            EnsureSize();
            slots[index] = null;
            Save();
        }

        /// <summary>返回用于展示的文字：名称优先，其次域名，空槽位为「+」。</summary>
        public static string GetDisplayName(QuickLinkItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Url))
            {
                return "+";
            }

            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                return item.Name.Trim();
            }

            var host = GetHost(item.Url);
            return string.IsNullOrEmpty(host) ? item.Url : host;
        }

        /// <summary>提取主机名（去 www. 前缀）。</summary>
        public static string GetHost(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }
            try
            {
                var raw = url.Contains("://") ? url : "http://" + url;
                var host = new Uri(raw).Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    host = host.Substring(4);
                }
                return host;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>后台抓取站点图标（favicon.ico），成功后保存并通知 UI。</summary>
        private static void FetchIconAsync(int index)
        {
            QuickLinkItem item = null;
            try
            {
                item = slots[index];
            }
            catch
            {
                return;
            }
            if (item == null)
            {
                return;
            }

            var host = GetHost(item.Url);
            if (string.IsNullOrEmpty(host))
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var dest = Path.Combine(AppPaths.IconsDir, "slot" + index + ".ico");
                    var tmp = dest + ".tmp";

                    foreach (var baseUrl in new[] { "https://" + host, "http://" + host })
                    {
                        try
                        {
                            var req = (HttpWebRequest)WebRequest.Create(baseUrl + "/favicon.ico");
                            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                            req.Timeout = 6000;
                            req.ReadWriteTimeout = 6000;
                            req.AllowAutoRedirect = true;

                            using (var resp = req.GetResponse())
                            using (var src = resp.GetResponseStream())
                            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                            {
                                var buffer = new byte[8192];
                                int n;
                                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    fs.Write(buffer, 0, n);
                                }
                            }

                            var bytes = File.ReadAllBytes(tmp);
                            // 粗校验：非空、小于 2MB、且首字节像 ico/png/gif/jpeg。
                            if (bytes.Length > 0 && bytes.Length < 2 * 1024 * 1024 &&
                                (bytes[0] == 0x00 || bytes[0] == 0x89 || bytes[0] == 0x47 || bytes[0] == 0xFF))
                            {
                                if (File.Exists(dest))
                                {
                                    File.Delete(dest);
                                }
                                File.Move(tmp, dest);
                                item.IconPath = dest;
                                Save();
                                IconUpdated?.Invoke();
                                return;
                            }

                            if (File.Exists(tmp))
                            {
                                File.Delete(tmp);
                            }
                        }
                        catch
                        {
                            if (File.Exists(tmp))
                            {
                                try { File.Delete(tmp); } catch { }
                            }
                        }
                    }
                }
                catch
                {
                    // 图标抓取失败不影响功能，按钮回退为默认「+」图标。
                }
            });
        }

        private static void EnsureSize()
        {
            while (slots.Count < SlotCount)
            {
                slots.Add(null);
            }
        }

        private static List<QuickLinkItem> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonConvert.DeserializeObject<List<QuickLinkItem>>(json)
                        ?? new List<QuickLinkItem>();

                    while (list.Count < SlotCount)
                    {
                        list.Add(null);
                    }
                    if (list.Count > SlotCount)
                    {
                        list.RemoveRange(SlotCount, list.Count - SlotCount);
                    }
                    return list;
                }
            }
            catch
            {
                // 文件损坏则回退到空槽位。
            }

            return NewEmpty();
        }

        private static List<QuickLinkItem> NewEmpty()
        {
            var list = new List<QuickLinkItem>();
            for (int i = 0; i < SlotCount; i++)
            {
                list.Add(null);
            }
            return list;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(slots, Formatting.Indented));
            }
            catch
            {
                // 忽略保存失败。
            }
        }
    }
}
