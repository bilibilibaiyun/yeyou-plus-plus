using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 书签栏 UserControl：横向 ScrollViewer 渲染顶层书签/文件夹。
    /// 文件夹点击展开递归 ContextMenu（独立置顶 HWND，浮于 CEF 之上）；
    /// 书签点击 NavigateRequested；右键支持打开/重命名/移动/删除；订阅 Favorites.Changed 即时刷新。
    /// </summary>
    public partial class BookmarkBar : UserControl
    {
        public event Action<string> NavigateRequested;

        public BookmarkBar()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                Favorites.Changed += OnFavoritesChanged;
                Refresh();
            };
            Unloaded += (s, e) => Favorites.Changed -= OnFavoritesChanged;
        }

        private void OnFavoritesChanged()
        {
            Dispatcher.InvokeAsync(Refresh);
        }

        /// <summary>重建书签栏内容。</summary>
        public void Refresh()
        {
            ItemsPanel.Children.Clear();
            var roots = Favorites.Roots;
            EmptyText.Visibility = roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var node in roots)
            {
                ItemsPanel.Children.Add(CreateNodeButton(node));
            }
        }

        private Button CreateNodeButton(FavoriteNode node)
        {
            var isBookmark = node.Kind == FavoriteNodeKind.Bookmark;
            var btn = new Button
            {
                Style = (Style)FindResource("BookmarkButton"),
                Height = 30,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 4, 8, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = node,
                Content = BuildContent(
                    isBookmark ? "\uE774" : "\uE8B7",
                    node.Name,
                    isBookmark ? (Brush)FindResource("Theme.Accent") : (Brush)FindResource("Theme.TextSecondary"))
            };

            if (isBookmark)
            {
                btn.ToolTip = node.Url;
                btn.Click += (s, e) => NavigateRequested?.Invoke(node.Url);
            }
            else
            {
                btn.Click += (s, e) => OpenFolderMenu(node, btn);
            }

            btn.ContextMenu = BuildActionMenu(node);
            return btn;
        }

        private StackPanel BuildContent(string glyph, string text, Brush iconBrush)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Style = CreatePressedWhiteStyle(iconBrush)
            });
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = CreatePressedWhiteStyle((Brush)FindResource("Theme.TextPrimary"))
            });
            return stack;
        }

        /// <summary>
        /// 生成「常态使用指定前景色、父级按钮按下时变为白色」的文本样式。
        /// 书签按钮模板里按下态只设置 ContentPresenter 的 TextElement.Foreground（继承值），
        /// 会被子 TextBlock 的局部 Foreground 压过；改用 DataTrigger 在按下时显式置白，
        /// 从而在蓝底按压态下让图标与文字都清晰可见。
        /// </summary>
        private static Style CreatePressedWhiteStyle(Brush normalBrush)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, normalBrush));

            var pressed = new DataTrigger
            {
                Binding = new Binding("IsPressed")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
                },
                Value = true
            };
            pressed.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
            style.Triggers.Add(pressed);
            return style;
        }

        /// <summary>点击文件夹展开递归子菜单（可多级嵌套，浮于 CEF 之上）。</summary>
        private void OpenFolderMenu(FavoriteNode folder, Button btn)
        {
            var menu = new ContextMenu();
            var children = folder.Children ?? new List<FavoriteNode>();
            if (children.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "（空文件夹）", IsEnabled = false });
            }
            else
            {
                foreach (var child in children)
                {
                    menu.Items.Add(CreateMenuItem(child));
                }
            }
            menu.PlacementTarget = btn;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>递归生成文件夹子菜单项（文件夹自动展开子菜单，书签点击导航）。</summary>
        private MenuItem CreateMenuItem(FavoriteNode node)
        {
            var item = new MenuItem { Header = node.Name, Tag = node };
            if (node.Kind == FavoriteNodeKind.Folder)
            {
                var children = node.Children ?? new List<FavoriteNode>();
                foreach (var child in children)
                {
                    item.Items.Add(CreateMenuItem(child));
                }
                if (children.Count == 0)
                {
                    item.Items.Add(new MenuItem { Header = "（空文件夹）", IsEnabled = false });
                }
            }
            else
            {
                item.Click += (s, e) => NavigateRequested?.Invoke(node.Url);
            }
            return item;
        }

        /// <summary>右键菜单：打开（书签）/重命名/移动/删除。</summary>
        private ContextMenu BuildActionMenu(FavoriteNode node)
        {
            var menu = new ContextMenu();
            if (node.Kind == FavoriteNodeKind.Bookmark)
            {
                var open = new MenuItem { Header = "打开" };
                open.Click += (s, e) => NavigateRequested?.Invoke(node.Url);
                menu.Items.Add(open);
            }

            var rename = new MenuItem { Header = "重命名…" };
            rename.Click += (s, e) =>
            {
                var name = PromptText("重命名", "名称", node.Name);
                if (name != null)
                {
                    Favorites.Rename(node, name);
                }
            };
            menu.Items.Add(rename);

            var move = new MenuItem { Header = "移动到文件夹…" };
            move.Click += (s, e) =>
            {
                if (PickFolder("移动到文件夹", node, out FavoriteNode target))
                {
                    Favorites.Move(node, target);
                }
            };
            menu.Items.Add(move);

            menu.Items.Add(new Separator());

            var delete = new MenuItem { Header = "删除" };
            delete.Click += (s, e) => DeleteWithConfirm(node);
            menu.Items.Add(delete);

            return menu;
        }

        private void DeleteWithConfirm(FavoriteNode node)
        {
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

        // ================= 辅助：代码内对话框 =================

        private class FolderChoice
        {
            public string Display { get; set; }
            public FavoriteNode Folder { get; set; }
        }

        private Window OwnerWindow
        {
            get { return Window.GetWindow(this); }
        }

        private string PromptText(string title, string label, string initial)
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = OwnerWindow,
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
                Owner = OwnerWindow,
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
    }
}
