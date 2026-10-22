using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YeyouPlusPlus
{
    /// <summary>一条已保存的密码记录。PasswordEncrypted 为 DPAPI 加密后的 Base64 密文。</summary>
    public class PasswordEntry
    {
        public string Site { get; set; }
        public string Username { get; set; }
        public string PasswordEncrypted { get; set; }
        public string Url { get; set; }
        public string CreatedUtc { get; set; }
        public string UpdatedUtc { get; set; }
    }

    /// <summary>
    /// 密码管理器：内存列表 + JSON 持久化（passwords.json）。
    /// 密码经 DPAPI（CurrentUser）加密后落盘，明文只驻内存。唯一键 = Site + Username。
    /// </summary>
    public static class PasswordManager
    {
        private static List<PasswordEntry> _items;

        static PasswordManager()
        {
            _items = Load();
        }

        private static string FilePath
        {
            get { return AppPaths.PasswordsFilePath; }
        }

        /// <summary>全部密码条目。</summary>
        public static IReadOnlyList<PasswordEntry> Items
        {
            get { return _items; }
        }

        /// <summary>数据目录变更后重新加载。</summary>
        public static void Reload()
        {
            _items = Load();
        }

        /// <summary>某网站（host）下的所有已存账号。</summary>
        public static List<PasswordEntry> GetForSite(string hostOrUrl)
        {
            var site = NormalizeHost(hostOrUrl);
            if (string.IsNullOrEmpty(site))
            {
                return new List<PasswordEntry>();
            }
            return _items.Where(x => x.Site == site).ToList();
        }

        /// <summary>按唯一键（Site + Username）查找，找不到返回 null。</summary>
        public static PasswordEntry Find(string site, string username)
        {
            var s = NormalizeHost(site);
            var u = (username ?? string.Empty).Trim();
            return _items.FirstOrDefault(x => x.Site == s && x.Username == u);
        }

        /// <summary>
        /// 保存新密码。已存在同键条目时不覆盖（返回 false）。成功返回 true。
        /// </summary>
        public static bool Save(string site, string username, string password, string url)
        {
            var s = NormalizeHost(site);
            var u = (username ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(password))
            {
                return false;
            }
            if (Find(s, u) != null)
            {
                return false;
            }

            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            _items.Add(new PasswordEntry
            {
                Site = s,
                Username = u,
                PasswordEncrypted = DpapiProtector.Protect(password),
                Url = string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim(),
                CreatedUtc = now,
                UpdatedUtc = now
            });
            Save();
            return true;
        }

        /// <summary>
        /// 更新密码：按 (site, oldUsername) 定位，改写用户名与密码（密码重新加密）。成功返回 true。
        /// </summary>
        public static bool Update(string site, string oldUsername, string username, string password)
        {
            var s = NormalizeHost(site);
            var entry = _items.FirstOrDefault(x => x.Site == s && x.Username == (oldUsername ?? string.Empty).Trim());
            if (entry == null || string.IsNullOrEmpty(password))
            {
                return false;
            }
            entry.Username = (username ?? string.Empty).Trim();
            entry.PasswordEncrypted = DpapiProtector.Protect(password);
            entry.UpdatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            Save();
            return true;
        }

        /// <summary>删除某条记录。</summary>
        public static void Delete(string site, string username)
        {
            var s = NormalizeHost(site);
            var u = (username ?? string.Empty).Trim();
            _items.RemoveAll(x => x.Site == s && x.Username == u);
            Save();
        }

        /// <summary>解密某条记录的密码，解密失败返回空串。</summary>
        public static string Decrypt(string passwordEncrypted)
        {
            return DpapiProtector.Unprotect(passwordEncrypted);
        }

        /// <summary>规范化站点为小写 host（去掉 www. 前缀）。无法解析时原样小写。</summary>
        public static string NormalizeHost(string hostOrUrl)
        {
            if (string.IsNullOrWhiteSpace(hostOrUrl))
            {
                return string.Empty;
            }
            try
            {
                var raw = hostOrUrl.Contains("://") ? hostOrUrl : "http://" + hostOrUrl;
                var host = new Uri(raw).Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    host = host.Substring(4);
                }
                return host.Trim().ToLowerInvariant();
            }
            catch
            {
                return hostOrUrl.Trim().ToLowerInvariant();
            }
        }

        private static List<PasswordEntry> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonConvert.DeserializeObject<List<PasswordEntry>>(json)
                        ?? new List<PasswordEntry>();
                    return list.Where(x => !string.IsNullOrWhiteSpace(x.Site)).ToList();
                }
            }
            catch
            {
                // 损坏则用空列表。
            }
            return new List<PasswordEntry>();
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
