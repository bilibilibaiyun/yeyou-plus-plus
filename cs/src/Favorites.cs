using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YeyouPlusPlus
{
    /// <summary>书签/文件夹节点类型。</summary>
    public enum FavoriteNodeKind
    {
        Bookmark = 0, // 旧数据无 Kind 字段 → 默认落到此值，天然迁移
        Folder = 1
    }

    /// <summary>
    /// 书签树的单一节点类型：用 Kind 区分「书签」与「文件夹」。
    /// 仅书签使用 Url；仅文件夹使用 Children（Load 后归一化为非 null）。
    /// </summary>
    public class FavoriteNode
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public FavoriteNodeKind Kind { get; set; }

        public string Name { get; set; }

        public string Url { get; set; }

        public List<FavoriteNode> Children { get; set; }
    }

    /// <summary>
    /// 旧版扁平收藏条目（仅用于旧 favorites.json 迁移兜底解析）。
    /// </summary>
    public class FavoriteItem
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }

    /// <summary>
    /// 收藏（书签）树：内存树 + JSON 单文件持久化（favorites.json）。
    /// 旧扁平 {Name,Url} 数据首次加载即平铺为顶层书签（幂等）；所有写操作统一 Save + 触发 Changed。
    /// </summary>
    public static class Favorites
    {
        private static List<FavoriteNode> _roots;

        static Favorites()
        {
            _roots = Load();
        }

        private static string FilePath
        {
            get { return Path.Combine(AppPaths.DataDir, "favorites.json"); }
        }

        /// <summary>顶层节点（书签 + 文件夹）。</summary>
        public static IReadOnlyList<FavoriteNode> Roots
        {
            get { return _roots; }
        }

        /// <summary>任意增删改移后触发（单一数据源同步，供书签栏/管理器即时刷新）。</summary>
        public static event Action Changed;

        /// <summary>数据目录变更后重新加载并通知订阅方。</summary>
        public static void Reload()
        {
            _roots = Load();
            Changed?.Invoke();
        }

        /// <summary>递归判断某 URL 是否已收藏。</summary>
        public static bool Contains(string url)
        {
            return FindBookmark(url) != null;
        }

        /// <summary>递归按 URL 查找书签节点，找不到返回 null。</summary>
        public static FavoriteNode FindBookmark(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }
            return FindBookmarkIn(_roots, url.Trim());
        }

        /// <summary>新增书签；folder=null 表示收藏到顶层。同 URL 去重（已存在则忽略）。</summary>
        public static void AddBookmark(string url, string name, FavoriteNode folder)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }
            url = url.Trim();
            if (FindBookmark(url) != null)
            {
                return;
            }

            var node = new FavoriteNode
            {
                Kind = FavoriteNodeKind.Bookmark,
                Name = string.IsNullOrWhiteSpace(name) ? url : name.Trim(),
                Url = url
            };

            if (folder != null && folder.Kind == FavoriteNodeKind.Folder)
            {
                if (folder.Children == null)
                {
                    folder.Children = new List<FavoriteNode>();
                }
                folder.Children.Add(node);
            }
            else
            {
                _roots.Add(node);
            }

            Save();
            Changed?.Invoke();
        }

        /// <summary>新增文件夹；parent=null 表示顶层。返回新建文件夹节点。</summary>
        public static FavoriteNode AddFolder(string name, FavoriteNode parent)
        {
            var node = new FavoriteNode
            {
                Kind = FavoriteNodeKind.Folder,
                Name = string.IsNullOrWhiteSpace(name) ? "新建文件夹" : name.Trim(),
                Children = new List<FavoriteNode>()
            };

            if (parent != null && parent.Kind == FavoriteNodeKind.Folder)
            {
                if (parent.Children == null)
                {
                    parent.Children = new List<FavoriteNode>();
                }
                parent.Children.Add(node);
            }
            else
            {
                _roots.Add(node);
            }

            Save();
            Changed?.Invoke();
            return node;
        }

        /// <summary>删除节点；文件夹级联删除其全部后代。</summary>
        public static void Remove(FavoriteNode node)
        {
            if (node == null)
            {
                return;
            }
            if (TryRemove(_roots, node))
            {
                Save();
                Changed?.Invoke();
            }
        }

        /// <summary>兼容旧星标语义：递归删除第一个匹配 URL 的书签。</summary>
        public static void RemoveByUrl(string url)
        {
            var node = FindBookmark(url);
            if (node != null)
            {
                Remove(node);
            }
        }

        /// <summary>移动节点到新父文件夹（newParent=null 表示顶层）。不能移入自身或后代。</summary>
        public static void Move(FavoriteNode node, FavoriteNode newParent)
        {
            if (node == null)
            {
                return;
            }
            // 不能移动到自身或其子树内（会形成环）。
            if (newParent != null && ContainsNode(node, newParent))
            {
                return;
            }
            if (!TryRemove(_roots, node))
            {
                return;
            }

            if (newParent == null)
            {
                _roots.Add(node);
            }
            else
            {
                if (newParent.Children == null)
                {
                    newParent.Children = new List<FavoriteNode>();
                }
                newParent.Children.Add(node);
            }

            Save();
            Changed?.Invoke();
        }

        /// <summary>重命名节点。</summary>
        public static void Rename(FavoriteNode node, string newName)
        {
            if (node == null || string.IsNullOrWhiteSpace(newName))
            {
                return;
            }
            node.Name = newName.Trim();
            Save();
            Changed?.Invoke();
        }

        /// <summary>深度优先扁平化所有文件夹节点（收藏弹窗选择器用）。</summary>
        public static List<FavoriteNode> GetAllFolders()
        {
            var result = new List<FavoriteNode>();
            CollectFolders(_roots, result);
            return result;
        }

        /// <summary>级联统计某节点（文件夹）下的书签数量（删除确认提示用）。</summary>
        public static int CountBookmarks(FavoriteNode node)
        {
            if (node == null)
            {
                return 0;
            }
            return CountBookmarksIn(node);
        }

        /// <summary>返回某文件夹的层级路径（如「页游 ▸ 动作类」），用于下拉选择器展示。</summary>
        public static string GetFolderPath(FavoriteNode node)
        {
            if (node == null)
            {
                return string.Empty;
            }
            var path = new List<string>();
            if (BuildPath(_roots, node, path))
            {
                return string.Join(" ▸ ", path);
            }
            return node.Name ?? string.Empty;
        }

        // ================= 内部实现 =================

        private static FavoriteNode FindBookmarkIn(List<FavoriteNode> nodes, string url)
        {
            foreach (var n in nodes)
            {
                if (n.Kind == FavoriteNodeKind.Bookmark &&
                    string.Equals(n.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    return n;
                }
                if (n.Kind == FavoriteNodeKind.Folder && n.Children != null)
                {
                    var found = FindBookmarkIn(n.Children, url);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            return null;
        }

        private static bool TryRemove(List<FavoriteNode> list, FavoriteNode node)
        {
            if (list.Remove(node))
            {
                return true;
            }
            foreach (var n in list)
            {
                if (n.Kind == FavoriteNodeKind.Folder && n.Children != null &&
                    TryRemove(n.Children, node))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>判断 target 是否位于 root 的子树内（含 root 自身）。</summary>
        private static bool ContainsNode(FavoriteNode root, FavoriteNode target)
        {
            if (root == null || target == null)
            {
                return false;
            }
            if (ReferenceEquals(root, target))
            {
                return true;
            }
            if (root.Children == null)
            {
                return false;
            }
            foreach (var c in root.Children)
            {
                if (ContainsNode(c, target))
                {
                    return true;
                }
            }
            return false;
        }

        private static void CollectFolders(List<FavoriteNode> nodes, List<FavoriteNode> result)
        {
            foreach (var n in nodes)
            {
                if (n.Kind == FavoriteNodeKind.Folder)
                {
                    result.Add(n);
                    if (n.Children != null)
                    {
                        CollectFolders(n.Children, result);
                    }
                }
            }
        }

        private static int CountBookmarksIn(FavoriteNode node)
        {
            if (node.Kind == FavoriteNodeKind.Bookmark)
            {
                return 1;
            }
            int count = 0;
            if (node.Children != null)
            {
                foreach (var c in node.Children)
                {
                    count += CountBookmarksIn(c);
                }
            }
            return count;
        }

        private static bool BuildPath(List<FavoriteNode> nodes, FavoriteNode target, List<string> path)
        {
            foreach (var n in nodes)
            {
                if (ReferenceEquals(n, target))
                {
                    path.Add(n.Name ?? string.Empty);
                    return true;
                }
                if (n.Kind == FavoriteNodeKind.Folder && n.Children != null)
                {
                    path.Add(n.Name ?? string.Empty);
                    if (BuildPath(n.Children, target, path))
                    {
                        return true;
                    }
                    path.RemoveAt(path.Count - 1);
                }
            }
            return false;
        }

        private static List<FavoriteNode> Load()
        {
            string json;
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new List<FavoriteNode>();
                }
                json = File.ReadAllText(FilePath);
            }
            catch
            {
                return new List<FavoriteNode>();
            }

            // 1) 新格式：旧扁平 {Name,Url} 缺 Kind/Children 时也会成功解析为顶层 Bookmark（天然迁移）。
            try
            {
                var nodes = JsonConvert.DeserializeObject<List<FavoriteNode>>(json);
                if (nodes != null)
                {
                    Normalize(nodes);
                    return nodes;
                }
            }
            catch
            {
                // 落入旧格式兜底。
            }

            // 2) 兜底：旧扁平 FavoriteItem 显式迁移并立即写回新格式（幂等）。
            var legacy = MigrateLegacy(json);
            if (legacy != null)
            {
                Save(legacy);
                return legacy;
            }

            return new List<FavoriteNode>();
        }

        private static List<FavoriteNode> MigrateLegacy(string json)
        {
            try
            {
                var items = JsonConvert.DeserializeObject<List<FavoriteItem>>(json);
                if (items == null)
                {
                    return null;
                }
                var nodes = items
                    .Where(f => !string.IsNullOrWhiteSpace(f.Url))
                    .Select(f => new FavoriteNode
                    {
                        Kind = FavoriteNodeKind.Bookmark,
                        Name = string.IsNullOrWhiteSpace(f.Name) ? f.Url.Trim() : f.Name.Trim(),
                        Url = f.Url.Trim()
                    })
                    .ToList();
                Normalize(nodes);
                return nodes;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>归一化：文件夹 Children 置非 null；丢弃空名/空 URL 的非法节点。</summary>
        private static void Normalize(List<FavoriteNode> nodes)
        {
            if (nodes == null)
            {
                return;
            }
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                var n = nodes[i];
                if (n == null || string.IsNullOrWhiteSpace(n.Name) ||
                    (n.Kind == FavoriteNodeKind.Bookmark && string.IsNullOrWhiteSpace(n.Url)))
                {
                    nodes.RemoveAt(i);
                    continue;
                }

                n.Name = n.Name.Trim();
                if (n.Kind == FavoriteNodeKind.Bookmark)
                {
                    n.Url = n.Url.Trim();
                    n.Children = null;
                }
                else
                {
                    if (n.Children == null)
                    {
                        n.Children = new List<FavoriteNode>();
                    }
                    Normalize(n.Children);
                }
            }
        }

        private static void Save(List<FavoriteNode> nodes = null)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore
                };
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(nodes ?? _roots, settings));
            }
            catch
            {
                // 忽略保存失败。
            }
        }
    }
}
