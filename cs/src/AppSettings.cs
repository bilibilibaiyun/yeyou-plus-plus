using Newtonsoft.Json;
using System;
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
