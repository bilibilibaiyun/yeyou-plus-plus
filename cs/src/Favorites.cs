using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 收藏夹：内存列表 + JSON 持久化。
    /// </summary>
    public class FavoriteItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }

    public static class Favorites
    {
        private static readonly List<FavoriteItem> items;

        static Favorites()
        {
            items = Load();
        }

        private static string FilePath
        {
            get { return Path.Combine(AppPaths.DataDir, "favorites.json"); }
        }

        public static IReadOnlyList<FavoriteItem> Items => items;

        /// <summary>数据目录变更后重新加载。</summary>
        public static void Reload()
        {
            var fresh = Load();
            items.Clear();
            items.AddRange(fresh);
        }

        public static void Add(string url, string name)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }
            if (items.Exists(f => f.Url == url))
            {
                return;
            }
            items.Insert(0, new FavoriteItem { Url = url, Name = string.IsNullOrWhiteSpace(name) ? url : name });
            Save();
        }

        public static void Remove(string url)
        {
            items.RemoveAll(f => f.Url == url);
            Save();
        }

        private static List<FavoriteItem> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonConvert.DeserializeObject<List<FavoriteItem>>(json) ?? new List<FavoriteItem>();
                }
            }
            catch
            {
                // 损坏则用空列表。
            }
            return new List<FavoriteItem>();
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(items, Formatting.Indented));
            }
            catch
            {
                // 忽略保存失败。
            }
        }
    }
}
