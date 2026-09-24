using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YeyouPlusPlus
{
    public partial class MainWindow : Window
    {
        // 缓存自动清理阈值（字节），便于调整。
        private const long CacheWarningBytes = 400L * 1024 * 1024; // 400MB 临界值
        private const long CacheDangerBytes = 800L * 1024 * 1024;  // 800MB 危险值

        private readonly BrowserHost browser;
        private readonly DispatcherTimer speedTimer;
        private readonly DispatcherTimer cacheTimer;
        private readonly DispatcherTimer titleTimer;
        private readonly TrayService tray;
        private bool cacheCheckRunning;
        private long lastCacheSize;
        private int frameCount;
        private int fps;
        private bool sidebarCollapsed;
        private List<ReleaseInfo> releases;

        public MainWindow()
        {
            InitializeComponent();

            // 创建浏览器宿主，初始为空白页（不加载任何网页）。
            browser = new BrowserHost("about:blank");
            BrowserContainer.Children.Add(browser);
            browser.AddressChanged += (s, e) => AddressBox.Text = browser.Address;
            // 窗口标题不显示网页名称（固定显示 帧率 + 缓存量）。
            browser.LoadingStateChanged += (s, e) => UpdateNavButtons();

            // 初始化倍速下拉。
            SpeedCombo.ItemsSource = new[] { "0.5x", "1x", "1.5x", "2x", "3x", "5x" };
            SpeedCombo.SelectedIndex = 1; // 默认 1x

            // 倍速应用定时器：每 5 秒下发一次倍率（同时确保新的 Flash 子进程被注入）。
            speedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            speedTimer.Tick += (s, e) => ApplySpeed();
            speedTimer.Start();

            // 缓存监控定时器：每 30 秒检查一次缓存目录大小（启动先立即查一次）。
            cacheTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            cacheTimer.Tick += CacheTimer_Tick;
            cacheTimer.Start();
            CacheTimer_Tick(null, EventArgs.Empty);

            // 帧率统计 + 标题刷新（每秒）。
            CompositionTarget.Rendering += (s, e) => frameCount++;
            titleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            titleTimer.Tick += (s, e) =>
            {
                fps = frameCount;
                frameCount = 0;
                UpdateTitle();
            };
            titleTimer.Start();

            // 站点图标下载完成后刷新快捷入口。
            QuickLinks.IconUpdated += OnQuickLinkIconUpdated;

            // 系统托盘。
            tray = new TrayService(ShowMainFromTray, ExitApp);

            RenderQuickLinks();
            RefreshSettingsView();

            // 启动默认执行一次检查更新（网络失败静默）。
            RunUpdateCheck(manual: false);
        }

        // ================= 标题（帧率 + 缓存量） =================

        private void UpdateTitle()
        {
            Title = string.Format("页游++   {0} FPS   ｜   缓存 {1}", fps, FormatBytes(lastCacheSize));
        }

        // ================= 视图切换 =================

        private void ShowView(string view)
        {
            HomePanel.Visibility = view == "home" ? Visibility.Visible : Visibility.Collapsed;
            BrowserView.Visibility = view == "browser" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = view == "settings" ? Visibility.Visible : Visibility.Collapsed;

            NavHomeButton.Tag = view == "home" ? "selected" : null;
            NavSettingsButton.Tag = view == "settings" ? "selected" : null;

            // 浏览器视图自动收拢侧边栏（游戏需要大屏），其他视图自动展开。
            var wantCollapsed = view == "browser";
            if (sidebarCollapsed != wantCollapsed)
            {
                ToggleSidebar();
            }

            if (view == "home")
            {
                RenderQuickLinks();
            }
            if (view == "settings")
            {
                RefreshSettingsView();
            }
        }

        private void NavHome_Click(object sender, RoutedEventArgs e) => ShowView("home");
        private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowView("settings");

        private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

        private void ToggleSidebar()
        {
            sidebarCollapsed = !sidebarCollapsed;
            SidebarColumn.Width = new GridLength(sidebarCollapsed ? 56 : 164);
            SidebarHeader.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            NavHomeText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            NavSettingsText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ToggleSidebarText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ToggleSidebarIcon.Text = sidebarCollapsed ? "\uE76A" : "\uE76B";
            var pad = sidebarCollapsed ? new Thickness(0, 9, 0, 9) : new Thickness(12, 9, 12, 9);
            var align = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            NavHomeButton.Padding = pad;
            NavSettingsButton.Padding = pad;
            NavHomeButton.HorizontalContentAlignment = align;
            NavSettingsButton.HorizontalContentAlignment = align;
            ToggleSidebarButton.Padding = pad;
            ToggleSidebarButton.HorizontalContentAlignment = align;
        }

        private void ShowMainFromTray()
        {
            if (!IsVisible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Maximized;
            }
            Activate();
        }

        private void ExitApp()
        {
            tray?.Dispose();
            Application.Current.Shutdown();
        }

        // ================= 浏览器 =================

        private void ApplySpeed()
        {
            if (SpeedCombo.SelectedItem is string s && s.EndsWith("x")
                && double.TryParse(s.TrimEnd('x'), out double speed))
            {
                SpeedHack.SetSpeed(speed);
            }
        }

        private void UpdateNavButtons()
        {
            BackButton.IsEnabled = browser.CanGoBack;
            ForwardButton.IsEnabled = browser.CanGoForward;
        }

        /// <summary>从主页面板切换到浏览器视图，并导航到指定内容。</summary>
        private void ShowBrowser(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            ShowView("browser");
            Navigate(input);
        }

        /// <summary>切回主页面板（不销毁浏览器，避免 CEF 反复初始化）。</summary>
        private void ShowHome() => ShowView("home");

        private void Navigate(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            input = input.Trim();
            string url;

            if (IsUrl(input))
            {
                url = input.Contains("://") ? input : "http://" + input;
            }
            else
            {
                url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(input);
            }

            browser.Load(url);
            AddressBox.Text = url;
        }

        private static bool IsUrl(string s)
        {
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // 简单判断：含点且无空格，视为网址。
            return s.Contains(".") && !s.Contains(" ") && !s.Contains("　");
        }

        private void HomeSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ShowBrowser(HomeSearchBox.Text);
            }
        }

        private void HomeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            HomeSearchHint.Visibility = string.IsNullOrEmpty(HomeSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Navigate(AddressBox.Text);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => browser.Back();
        private void ForwardButton_Click(object sender, RoutedEventArgs e) => browser.Forward();
        private void ReloadButton_Click(object sender, RoutedEventArgs e) => browser.Reload();
        private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowHome();

        private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 倍速下拉变化：立即应用。
            ApplySpeed();
        }

        // ================= 页面缩放 =================

        private void ZoomButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            foreach (var p in new[] { 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200 })
            {
                var percent = p;
                var mi = new MenuItem { Header = percent + "%", FontSize = 13 };
                mi.Click += (s, ev) => browser.SetZoomPercent(percent);
                menu.Items.Add(mi);
            }
            menu.PlacementTarget = ZoomButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // ================= 收藏 =================

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            // 后台清理，完成后回 UI 线程更新提示。
            CacheManager.ClearCache(() =>
            {
                SetStatus("缓存已清理");
                MessageBox.Show("缓存已清理", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            Favorites.Add(browser.Address, browser.Title ?? browser.Address);
            SetStatus("已收藏当前页");
        }

        private void FavoritesButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new FavoritesWindow();
            win.Owner = this;
            win.NavigateRequested += url => ShowBrowser(url);
            win.ShowDialog();
        }

        // ================= 快捷入口 =================

        private void OnQuickLinkIconUpdated()
        {
            Dispatcher.InvokeAsync(RenderQuickLinks);
        }

        private void RenderQuickLinks()
        {
            QuickLinkPanel.Children.Clear();

            for (int i = 0; i < QuickLinks.SlotCount; i++)
            {
                int index = i;
                var item = QuickLinks.Get(index);
                var display = QuickLinks.GetDisplayName(item);

                var stack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                if (item == null)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "\uE710",
                        FontFamily = (FontFamily)FindResource("IconFont"),
                        FontSize = 26,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)FindResource("Theme.TextSecondary")
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = "添加",
                        FontSize = 12,
                        Margin = new Thickness(0, 7, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)FindResource("Theme.TextSecondary")
                    });
                }
                else
                {
                    UIElement icon = null;
                    if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
                    {
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.UriSource = new Uri(item.IconPath);
                            bi.DecodePixelWidth = 40;
                            bi.EndInit();
                            bi.Freeze();
                            icon = new Image { Source = bi, Width = 40, Height = 40 };
                        }
                        catch
                        {
                            icon = null;
                        }
                    }
                    if (icon == null)
                    {
                        icon = new TextBlock
                        {
                            Text = "\uE774",
                            FontFamily = (FontFamily)FindResource("IconFont"),
                            FontSize = 28,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = (Brush)FindResource("Theme.Accent")
                        };
                    }
                    stack.Children.Add(icon);
                    stack.Children.Add(new TextBlock
                    {
                        Text = display,
                        FontSize = 12,
                        Margin = new Thickness(0, 8, 0, 0),
                        MaxWidth = 92,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)FindResource("Theme.TextPrimary")
                    });
                }

                var btn = new Button
                {
                    Width = 106,
                    Height = 98,
                    Margin = new Thickness(7),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = index,
                    Content = stack,
                    ToolTip = item != null ? item.Url : "点击添加快捷入口"
                };
                btn.Click += QuickLinkButton_Click;

                if (item != null)
                {
                    // 已保存槽位支持右键删除。
                    var menu = new ContextMenu();
                    var deleteItem = new MenuItem { Header = "删除", Tag = index, FontSize = 13 };
                    deleteItem.Click += (s, e) =>
                    {
                        QuickLinks.Remove((int)deleteItem.Tag);
                        RenderQuickLinks();
                        SetStatus("已删除快捷入口");
                    };
                    menu.Items.Add(deleteItem);
                    btn.ContextMenu = menu;
                }

                QuickLinkPanel.Children.Add(btn);
            }
        }

        private void QuickLinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is int index))
            {
                return;
            }

            var item = QuickLinks.Get(index);
            if (item == null)
            {
                OpenQuickLinkWindow(index);
            }
            else
            {
                ShowBrowser(item.Url);
            }
        }

        private void OpenQuickLinkWindow(int index)
        {
            var win = new QuickLinkWindow();
            win.Owner = this;
            if (win.ShowDialog() == true)
            {
                QuickLinks.Set(index, win.Url, win.LinkName);
                RenderQuickLinks();
                SetStatus("已保存快捷入口");
            }
        }

        // ================= 缓存自动清理 =================

        private void CacheTimer_Tick(object sender, EventArgs e)
        {
            if (cacheCheckRunning)
            {
                return;
            }
            cacheCheckRunning = true;

            var dir = AppPaths.CacheDir;
            Task.Run(() => CacheManager.GetDirectorySize(dir))
                .ContinueWith(t =>
                {
                    cacheCheckRunning = false;
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        return;
                    }
                    lastCacheSize = t.Result;
                    HandleCacheSize(lastCacheSize);
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void HandleCacheSize(long size)
        {
            if (size >= CacheDangerBytes)
            {
                // 后台自动清理，完成后回 UI 线程更新状态栏。
                CacheManager.ClearCache(() =>
                {
                    SetStatus("缓存已超过 " + FormatBytes(CacheDangerBytes) + "，已自动清理");
                });
            }
            else if (size >= CacheWarningBytes)
            {
                SetStatus("缓存已较大（" + FormatBytes(size) + "），建议清理");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
            {
                return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0") + " GB";
            }
            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            }
            if (bytes >= 1024L)
            {
                return (bytes / 1024.0).ToString("0.0") + " KB";
            }
            return bytes + " B";
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text ?? string.Empty;
        }

        // ================= 设置 =================

        private void RefreshSettingsView()
        {
            DataDirBox.Text = AppPaths.DataDir;
            DownloadDirBox.Text = AppPaths.DownloadDir;
            CurrentVersionText.Text = "当前版本 v" + UpdateChecker.CurrentVersion;
            if (releases != null)
            {
                RenderReleaseList();
            }
        }

        private static string PickFolder(string description)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = true
            })
            {
                return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        private static void CopyDir(string src, string dst)
        {
            if (!Directory.Exists(src))
            {
                return;
            }
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
            {
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
            foreach (var d in Directory.GetDirectories(src))
            {
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            }
        }

        private void ChangeDataDirButton_Click(object sender, RoutedEventArgs e)
        {
            var newPath = PickFolder("选择新的数据存储路径");
            if (string.IsNullOrEmpty(newPath))
            {
                return;
            }

            var oldDir = AppPaths.DataDir;
            if (string.Equals(Path.GetFullPath(newPath).TrimEnd('\\'),
                              Path.GetFullPath(oldDir).TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("新路径与当前路径相同。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                "数据（收藏、快捷入口、图标、下载）将迁移到：\n" + newPath + "\n\n是否继续？",
                "页游++", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(newPath);
                foreach (var f in new[] { "favorites.json", "quicklinks.json", "settings.json" })
                {
                    var src = Path.Combine(oldDir, f);
                    if (File.Exists(src))
                    {
                        File.Copy(src, Path.Combine(newPath, f), true);
                    }
                }
                CopyDir(Path.Combine(oldDir, "icons"), Path.Combine(newPath, "icons"));
                CopyDir(Path.Combine(oldDir, "downloads"), Path.Combine(newPath, "downloads"));

                AppSettingsStore.Current.DataDirPath = newPath;
                AppSettingsStore.Save();
                AppSettingsStore.WriteDataDirToRegistry(newPath);

                QuickLinks.Reload();
                Favorites.Reload();
                RenderQuickLinks();
                RefreshSettingsView();
                SetStatus("数据存储路径已更新");
                MessageBox.Show("数据存储路径已更新。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("迁移失败：" + ex.Message, "页游++", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ChangeDownloadDirButton_Click(object sender, RoutedEventArgs e)
        {
            var newPath = PickFolder("选择下载保存路径");
            if (string.IsNullOrEmpty(newPath))
            {
                return;
            }

            AppSettingsStore.Current.DownloadDirPath = newPath;
            AppSettingsStore.Save();
            RefreshSettingsView();
            SetStatus("下载保存路径已更新");
        }

        private void RepoLink_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = UpdateChecker.RepoUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                // 忽略打开失败。
            }
        }

        // ================= 检查更新 / 版本选择 =================

        private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            RunUpdateCheck(manual: true);
        }

        private void RunUpdateCheck(bool manual)
        {
            CheckUpdateButton.IsEnabled = false;
            if (manual)
            {
                UpdateStatusText.Text = "正在检查更新…";
            }

            Task.Run(() =>
            {
                try
                {
                    return UpdateChecker.GetReleases();
                }
                catch
                {
                    return null;
                }
            })
            .ContinueWith(t =>
            {
                CheckUpdateButton.IsEnabled = true;
                if (t.IsFaulted || t.Result == null || t.Result.Count == 0)
                {
                    UpdateBadge.Visibility = Visibility.Collapsed;
                    if (manual)
                    {
                        UpdateStatusText.Text = "检查失败，请检查网络后重试。";
                    }
                    return;
                }
                OnReleasesLoaded(t.Result, manual);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnReleasesLoaded(List<ReleaseInfo> list, bool manual)
        {
            releases = list;
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                RenderReleaseList();
            }

            var stable = UpdateChecker.LatestStable(list);
            var hasNew = stable != null && UpdateChecker.IsNewer(stable.Tag, UpdateChecker.CurrentVersion);
            UpdateBadge.Visibility = hasNew ? Visibility.Visible : Visibility.Collapsed;

            if (hasNew)
            {
                UpdateStatusText.Text = "发现新的稳定版本 v" + stable.Tag +
                    "（当前 v" + UpdateChecker.CurrentVersion + "）。可在下方版本列表中选择安装。";
                SetStatus("发现新的稳定版本 v" + stable.Tag + "，可在设置中更新");
            }
            else if (manual)
            {
                UpdateStatusText.Text = "当前已是最新版本。";
            }
            else
            {
                UpdateStatusText.Text = "自动检查完成，当前为最新版本。";
            }
        }

        private void RenderReleaseList()
        {
            ReleaseList.Items.Clear();
            if (releases == null)
            {
                return;
            }

            foreach (var r in releases)
            {
                var grid = new Grid { Margin = new Thickness(0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Orientation = Orientation.Horizontal };
                left.Children.Add(new TextBlock
                {
                    Text = "v" + r.Tag,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                left.Children.Add(new Border
                {
                    Background = new SolidColorBrush(r.IsStable
                        ? (Color)ColorConverter.ConvertFromString("#E2F0E4")
                        : (Color)ColorConverter.ConvertFromString("#EDEDF2")),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(6, 1, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = r.TypeText,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(r.IsStable
                            ? (Color)ColorConverter.ConvertFromString("#2B7A3F")
                            : (Color)ColorConverter.ConvertFromString("#6A6E7C"))
                    }
                });
                left.Children.Add(new TextBlock
                {
                    Text = r.PublishedAt == DateTime.MinValue
                        ? string.Empty
                        : r.PublishedAt.ToString("yyyy-MM-dd"),
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Theme.TextSecondary")
                });
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                var sizeText = new TextBlock
                {
                    Text = FormatBytes(r.AssetSize),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Theme.TextSecondary")
                };
                Grid.SetColumn(sizeText, 1);
                grid.Children.Add(sizeText);

                ReleaseList.Items.Add(new ListBoxItem { Tag = r, Content = grid });
            }
        }

        private void ReleaseList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            InstallSelected();
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            InstallSelected();
        }

        private void InstallSelected()
        {
            if (!(ReleaseList.SelectedItem is ListBoxItem lbi) || !(lbi.Tag is ReleaseInfo r))
            {
                MessageBox.Show("请先在列表中选择一个版本。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirm;
            if (r.IsStable)
            {
                confirm = MessageBox.Show(
                    "将下载并覆盖安装 v" + r.Tag + "（配置与数据保留）。\n\n是否继续？",
                    "页游++", MessageBoxButton.YesNo, MessageBoxImage.Question);
            }
            else
            {
                confirm = MessageBox.Show(
                    "v" + r.Tag + " 是测试版，可能不够稳定，一般不建议使用。\n\n仍要安装吗？",
                    "页游++", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            InstallButton.IsEnabled = false;
            UpdateStatusText.Text = "正在下载 v" + r.Tag + " … 0%";

            var destDir = Path.Combine(AppPaths.DataDir, "update");
            var dest = Path.Combine(destDir, "YeyouPlusPlus_" + r.Tag + "_Setup.exe");

            Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                UpdateChecker.DownloadFile(r.AssetUrl, dest,
                    pct => Dispatcher.InvokeAsync(() =>
                        UpdateStatusText.Text = "正在下载 v" + r.Tag + " … " + pct + "%"));
            })
            .ContinueWith(t =>
            {
                InstallButton.IsEnabled = true;
                if (t.IsFaulted)
                {
                    var msg = t.Exception != null && t.Exception.InnerException != null
                        ? t.Exception.InnerException.Message
                        : "未知错误";
                    UpdateStatusText.Text = "下载失败：" + msg;
                    MessageBox.Show("下载失败：" + msg, "页游++", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                UpdateStatusText.Text = "下载完成，正在启动安装…";
                if (UpdateChecker.LaunchInstaller(dest))
                {
                    // 让安装程序接管（覆盖安装后自动重启新版）。
                    tray?.Dispose();
                    Application.Current.Shutdown();
                }
                else
                {
                    UpdateStatusText.Text = "安装程序启动失败。";
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        protected override void OnClosed(EventArgs e)
        {
            QuickLinks.IconUpdated -= OnQuickLinkIconUpdated;
            speedTimer?.Stop();
            cacheTimer?.Stop();
            titleTimer?.Stop();
            tray?.Dispose();
            browser.Dispose();
            base.OnClosed(e);
        }
    }
}
