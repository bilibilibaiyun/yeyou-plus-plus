using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 应用设置（数据存储路径 / 下载保存路径）。
    /// 配置文件固定存放在程序目录 data\settings.json（不随数据目录迁移），
    /// 自定义数据目录同时写入注册表，供卸载程序清理使用。
    /// </summary>
    public class AppSettings
    {
        /// <summary>自定义数据存储路径（空 = 程序目录 data）。</summary>
        public string DataDirPath { get; set; }

        /// <summary>下载保存路径（空 = 数据目录 downloads）。</summary>
        public string DownloadDirPath { get; set; }

        /// <summary>是否使用夜间模式（false = 日间）。</summary>
        public bool IsDarkMode { get; set; }

        /// <summary>视频背景隐藏主题是否已解锁（主页连点 5 次彩蛋，解锁后持久保持）。</summary>
        public bool VideoThemeUnlocked { get; set; }

        /// <summary>视频背景隐藏主题是否激活（关闭软件重开后恢复视频背景）。</summary>
        public bool VideoThemeActive { get; set; }

        /// <summary>缓存自动清理阈值（MB），默认 800，下限 100。</summary>
        public int CacheCleanThresholdMB { get; set; } = 800;

        /// <summary>是否显示书签栏（默认显示）。</summary>
        public bool ShowBookmarkBar { get; set; } = true;

        /// <summary>搜索引擎 key（bing = 必应国内版 / baidu = 百度 / sogou = 搜狗）。</summary>
        public string SearchEngine { get; set; } = "bing";
    }

    /// <summary>
    /// 搜索引擎静态配置：engine key → 搜索 URL 模板。
    /// 默认使用必应国内版（cn.bing.com），避免部分企业/校园网的「禁止访问国外网站」
    /// 上网行为管理策略拦截国际版必应并返回提示页。
    /// </summary>
    public static class SearchEngines
    {
        /// <summary>默认搜索引擎 key。</summary>
        public const string DefaultKey = "bing";

        private static readonly Dictionary<string, string> Templates =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "bing", "https://cn.bing.com/search?q=" },
                { "baidu", "https://www.baidu.com/s?wd=" },
                { "sogou", "https://www.sogou.com/web?query=" }
            };

        /// <summary>根据 key 取搜索 URL 模板；未知 key 回退到必应国内版。</summary>
        public static string GetTemplate(string key)
        {
            if (!string.IsNullOrEmpty(key) && Templates.TryGetValue(key, out var template))
            {
                return template;
            }
            return Templates[DefaultKey];
        }
    }

    public static class AppSettingsStore
    {
        private const string RegKey = @"Software\YeyouPlusPlus";
        private static readonly string filePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "settings.json");

        private static AppSettings current;

        public static AppSettings Current
        {
            get { return current ?? (current = Load()); }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, JsonConvert.SerializeObject(Current, Formatting.Indented));
            }
            catch
            {
                // 忽略保存失败。
            }
        }

        /// <summary>把自定义数据目录写入注册表（卸载时读取并删除）。</summary>
        public static void WriteDataDirToRegistry(string path)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key.SetValue("DataDirPath", path ?? string.Empty, RegistryValueKind.String);
                }
            }
            catch
            {
                // 忽略注册表写入失败。
            }
        }

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(filePath))
                {
                    return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(filePath))
                        ?? new AppSettings();
                }
            }
            catch
            {
                // 损坏则使用默认设置。
            }
            return new AppSettings();
        }
    }
}
