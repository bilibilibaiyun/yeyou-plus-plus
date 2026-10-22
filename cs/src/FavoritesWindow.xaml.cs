using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace YeyouPlusPlus
{
    /// <summary>按节点 Kind 选择书签/文件夹模板。</summary>
    public class FavoriteNodeTemplateSelector : DataTemplateSelector
    {
        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is FavoriteNode node && container is FrameworkElement fe)
            {
                var key = node.Kind == FavoriteNodeKind.Folder ? "FolderTemplate" : "BookmarkTemplate";
                return fe.FindResource(key) as DataTemplate;
            }
            return base.SelectTemplate(item, container);
        }
    }

    /// <summary>
    /// 收藏夹树形管理器：TreeView 展示书签/文件夹，支持新建文件夹、重命名、移动、级联删除。
    /// 订阅 Favorites.Changed 即时同步；写操作统一走 Favorites（单一数据源）。
    /// </summary>
    public partial class FavoritesWindow : Window
    {
        public event Action<string> NavigateRequested;

        public FavoritesWindow()
        {
            InitializeComponent();
            RefreshTree();
            Loaded += (s, e) => Favorites.Changed += OnFavoritesChanged;
            Closed += (s, e) => Favorites.Changed -= OnFavoritesChanged;
        }

        private void OnFavoritesChanged()
        {
            Dispatcher.InvokeAsync(RefreshTree);
        }

        private void RefreshTree()
        {
            Tree.ItemsSource = null;
            Tree.ItemsSource = Favorites.Roots;
        }

        private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(Tree.SelectedItem is FavoriteNode node))
            {
                return;
            }
            if (node.Kind == FavoriteNodeKind.Bookmark)
            {
                NavigateRequested?.Invoke(node.Url);
                Close();
                return;
            }
            // 双击文件夹：展开/折叠。
            var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (tvi != null)
            {
                tvi.IsExpanded = !tvi.IsExpanded;
            }
        }

        private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 右键先选中对应节点，再弹出节点上下文菜单。
            var tvi = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (tvi != null)
            {
                tvi.IsSelected = true;
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (Tree.SelectedItem is FavoriteNode node && node.Kind == FavoriteNodeKind.Bookmark)
            {
                NavigateRequested?.Invoke(node.Url);
                Close();
            }
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var parent = Tree.SelectedItem is FavoriteNode sel && sel.Kind == FavoriteNodeKind.Folder
                ? sel
                : null;
            var name = PromptText("新建文件夹", "文件夹名称", string.Empty);
            if (name != null)
            {
                Favorites.AddFolder(name, parent);
            }
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (!(Tree.SelectedItem is FavoriteNode node))
            {
                MessageBox.Show("请先选择一个节点。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var name = PromptText("重命名", "名称", node.Name);
            if (name != null)
            {
                Favorites.Rename(node, name);
            }
        }

        private void Move_Click(object sender, RoutedEventArgs e)
        {
            if (!(Tree.SelectedItem is FavoriteNode node))
            {
                MessageBox.Show("请先选择一个节点。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (PickFolder("移动到文件夹", node, out FavoriteNode target))
            {
                Favorites.Move(node, target);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!(Tree.SelectedItem is FavoriteNode node))
            {
                MessageBox.Show("请先选择一个节点。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string msg;
            if (node.Kind == FavoriteNodeKind.Folder)
            {
                int n = Favorites.CountBookmarks(node);
                msg = "删除文件夹「" + node.Name + "」及其中的 " + n + " 个书签？此操作不可撤销。";
            }
            else
            {
                msg = "删除书签「" + node.Name + "」？";
            }

            if (MessageBox.Show(msg, "页游++", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                Favorites.Remove(node);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ================= 辅助：代码内对话框 =================

        private class FolderChoice
        {
            public string Display { get; set; }
            public FavoriteNode Folder { get; set; }
        }

        private string PromptText(string title, string label, string initial)
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("Theme.WindowBg"),
                Foreground = (Brush)FindResource("Theme.TextPrimary")
            };
            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (Brush)FindResource("Theme.TextSecondary")
            });
            var box = new TextBox
            {
                Text = initial ?? string.Empty,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            stack.Children.Add(box);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancel = new Button { Content = "取消", Width = 80, Height = 32 };
            cancel.Click += (s, e) => win.DialogResult = false;
            var ok = new Button
            {
                Content = "确定",
                Width = 80,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("PrimaryButton")
            };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(box.Text))
                {
                    MessageBox.Show("名称不能为空。", "页游++", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                win.DialogResult = true;
            };
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(ok);
            stack.Children.Add(btnRow);

            win.Content = stack;
            return win.ShowDialog() == true ? box.Text.Trim() : null;
        }

        private bool PickFolder(string title, FavoriteNode self, out FavoriteNode folder)
        {
            folder = null;
            var options = new List<FolderChoice>
            {
                new FolderChoice { Display = "（顶层）", Folder = null }
            };
            foreach (var f in Favorites.GetAllFolders())
            {
                if (self != null && (ReferenceEquals(f, self) || IsUnder(self, f)))
                {
                    continue;
                }
                options.Add(new FolderChoice { Display = Favorites.GetFolderPath(f), Folder = f });
            }

            var win = new Window
            {
                Title = title,
                Width = 380,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("Theme.WindowBg"),
                Foreground = (Brush)FindResource("Theme.TextPrimary")
            };
            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(new TextBlock
            {
                Text = "选择目标文件夹",
                FontSize = 12,
                Foreground = (Brush)FindResource("Theme.TextSecondary")
            });
            var combo = new ComboBox
            {
                ItemsSource = options,
                DisplayMemberPath = "Display",
                SelectedIndex = 0,
                Height = 32,
                Margin = new Thickness(0, 8, 0, 0)
            };
            stack.Children.Add(combo);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancel = new Button { Content = "取消", Width = 80, Height = 32 };
            cancel.Click += (s, e) => win.DialogResult = false;
            var ok = new Button
            {
                Content = "确定",
                Width = 80,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("PrimaryButton")
            };
            ok.Click += (s, e) => win.DialogResult = true;
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(ok);
            stack.Children.Add(btnRow);

            win.Content = stack;
            if (win.ShowDialog() == true && combo.SelectedItem is FolderChoice choice)
            {
                folder = choice.Folder;
                return true;
            }
            return false;
        }

        private static bool IsUnder(FavoriteNode root, FavoriteNode target)
        {
            if (root == null || root.Children == null)
            {
                return false;
            }
            foreach (var c in root.Children)
            {
                if (ReferenceEquals(c, target) || IsUnder(c, target))
                {
                    return true;
                }
            }
            return false;
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t)
                {
                    return t;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
