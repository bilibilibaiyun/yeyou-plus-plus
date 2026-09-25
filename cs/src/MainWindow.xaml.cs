using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CefSharp;

namespace YeyouPlusPlus
{
    public partial class MainWindow : Window
    {
        // 缓存自动清理阈值（字节），便于调整。
        private const long CacheWarningBytes = 400L * 1024 * 1024; // 400MB 临界值
        private const long CacheDangerBytes = 800L * 1024 * 1024;  // 800MB 危险值

        /// <summary>一个浏览器标签页。</summary>
        private class BrowserTab
        {
            public BrowserHost Host;
            public string Title;
            public bool HasNavigated; // 是否已真正导航过（非 about:blank）
        }

        private readonly List<BrowserTab> tabs = new List<BrowserTab>();
        private int activeTabIndex = -1;

        private readonly DispatcherTimer speedTimer;
        private readonly DispatcherTimer cacheTimer;
        private readonly DispatcherTimer titleTimer;
        private readonly TrayService tray;
        private bool cacheCheckRunning;
        private long lastCacheSize;
        private int frameCount;
        private int fps;
        private bool sidebarCollapsed;
        private bool updatingZoomUi;
        private List<ReleaseInfo> releases;
        private readonly DispatcherTimer zoomDebounce;
        private double pendingZoomPercent = 100;
        private bool shadowSidebarExpanded;

        /// <summary>当前激活标签的浏览器宿主。</summary>
        private BrowserHost CurrentHost
        {
            get { return activeTabIndex >= 0 && activeTabIndex < tabs.Count ? tabs[activeTabIndex].Host : null; }
        }

        public MainWindow()
        {
            InitializeComponent();

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

            // 缩放 debounce：拖动滑块停止 150ms 后才真正应用缩放并写入记忆，
            // 避免拖动过程中高频调用 CEF SetZoomLevel + 磁盘写入导致的卡顿。
            zoomDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            zoomDebounce.Tick += (s, e) =>
            {
                zoomDebounce.Stop();
                ApplyZoomNow();
            };

            // 站点图标下载完成后刷新快捷入口。
            QuickLinks.IconUpdated += OnQuickLinkIconUpdated;

            // 系统托盘。
            tray = new TrayService(ShowMainFromTray, ExitApp);

            // 初始创建一个空标签（启动仍显示主页）。
            CreateTab();
            activeTabIndex = 0;
            // [修复] RenderTabs 延迟到窗口 Loaded 后执行：构造期向常驻 TabStrip
            // 填充内容会干扰后续 CEF HwndHost 的 SetParent 呈现（浏览器空白）。
            Loaded += (s, e) => RenderTabs();

            RenderQuickLinks();
            RefreshSettingsView();
            UpdateThemeToggleIcon();

            // 启动默认执行一次检查更新（网络失败静默）。
            RunUpdateCheck(manual: false);
        }

        // ================= 标题（帧率 + 缓存量） =================

        private void UpdateTitle()
        {
            Title = string.Format("页游++   {0} FPS   ｜   缓存 {1}", fps, FormatBytes(lastCacheSize));
        }

        // ================= 标签系统 =================

        private int CreateTab(string initialUrl = "about:blank", IRequestContext requestContext = null)
        {
            // 初始 URL 直接传给 ChromiumWebBrowser 构造函数（CEF 原生机制，时序安全）；
            // 若在 OnAfterCreated 时机手动 Load 会被主 frame 未就绪吞掉（影子标签空白根因）。
            var host = new BrowserHost(initialUrl, requestContext);
            var tab = new BrowserTab { Host = host };
            tab.HasNavigated = initialUrl != "about:blank";

            host.AddressChanged += (s, e) => Dispatcher.InvokeAsync(() => OnTabAddressChanged(tab));
            host.TitleChanged += (s, e) => Dispatcher.InvokeAsync(() => OnTabTitleChanged(tab));
            host.LoadingStateChanged += (s, e) => Dispatcher.InvokeAsync(() => OnTabLoadingStateChanged(tab));

            tabs.Add(tab);
            BrowserContainer.Children.Add(host);
            return tabs.Count - 1;
        }

        private void OnTabAddressChanged(BrowserTab tab)
        {
            var addr = tab.Host.Address;
            if (!string.IsNullOrEmpty(addr) && addr != "about:blank")
            {
                tab.HasNavigated = true;
            }
            if (IsActiveTab(tab))
            {
                AddressBox.Text = addr;
                UpdateNavButtons();
                UpdateFavoriteButton();
                CheckShadowAutoExpand();
            }
        }

        private void OnTabTitleChanged(BrowserTab tab)
        {
            tab.Title = tab.Host.Title;
            RenderTabs();
        }

        private void OnTabLoadingStateChanged(BrowserTab tab)
        {
            if (!IsActiveTab(tab))
            {
                return;
            }
            UpdateNavButtons();
            // 页面开始加载时立即触发一次注入检查：进入副本等场景会新建
            // Flash 插件进程，及时注入可避免等 5 秒周期 tick 造成变速空窗。
            if (tab.Host.IsLoading)
            {
                SpeedHack.EnsureInject();
            }
            else
            {
                ApplyStoredZoom(tab);
            }
        }

        private bool IsActiveTab(BrowserTab tab)
        {
            return activeTabIndex >= 0 && activeTabIndex < tabs.Count && tabs[activeTabIndex] == tab;
        }

        private void SwitchTab(int index)
        {
            if (index < 0 || index >= tabs.Count)
            {
                return;
            }
            activeTabIndex = index;
            ApplyActiveTab();
            ShowView("browser");
        }

        private void ApplyActiveTab()
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                tabs[i].Host.Visibility = i == activeTabIndex ? Visibility.Visible : Visibility.Collapsed;
            }
            RenderTabs();
            var h = CurrentHost;
            if (h != null)
            {
                AddressBox.Text = h.Address;
                UpdateNavButtons();
                UpdateFavoriteButton();
                CheckShadowAutoExpand();
            }
        }

        private void CloseTab(int index)
        {
            if (index < 0 || index >= tabs.Count)
            {
                return;
            }
            var host = tabs[index].Host;
            tabs.RemoveAt(index);
            BrowserContainer.Children.Remove(host);
            host.Dispose();

            if (tabs.Count == 0)
            {
                activeTabIndex = -1;
                ShowView("home");
                return;
            }

            activeTabIndex = Math.Min(index, tabs.Count - 1);
            ApplyActiveTab();
        }

        private void NewTabButton_Click(object sender, RoutedEventArgs e)
        {
            activeTabIndex = CreateTab();
            ApplyActiveTab();
            ShowView("browser");
        }

        /// <summary>在标签中打开 URL：复用已初始化的空白标签，否则新建标签直接以目标 URL 初始化。</summary>
        private void OpenInTab(string url)
        {
            int idx = -1;
            for (int i = 0; i < tabs.Count; i++)
            {
                // 只复用已完成初始化的空白标签；未初始化的复用同样会踩「过早 Load」坑。
                if (!tabs[i].HasNavigated && tabs[i].Host.Address == "about:blank"
                    && tabs[i].Host.IsBrowserInitializedForDiag)
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                // 新建标签：直接以目标 URL 初始化（CEF 原生机制，避免过早 Load 被吞）。
                idx = CreateTab(url);
                activeTabIndex = idx;
                ApplyActiveTab();
                ShowView("browser");
                AddressBox.Text = url;
                return;
            }
            activeTabIndex = idx;
            ApplyActiveTab();
            ShowView("browser");
            NavigateOnTab(idx, url);
        }

        private void NavigateOnTab(int index, string input)
        {
            if (string.IsNullOrWhiteSpace(input) || index < 0 || index >= tabs.Count)
            {
                return;
            }
            input = input.Trim();
            string url = IsUrl(input)
                ? (input.Contains("://") ? input : "http://" + input)
                : "https://www.bing.com/search?q=" + Uri.EscapeDataString(input);
            tabs[index].Host.Load(url);
            tabs[index].HasNavigated = true;
            if (index == activeTabIndex)
            {
                AddressBox.Text = url;
            }
        }

        private void RenderTabs()
        {
            TabStrip.Children.Clear();
            for (int i = 0; i < tabs.Count; i++)
            {
                int idx = i;
                var tab = tabs[i];
                var title = string.IsNullOrWhiteSpace(tab.Title) ? "新标签页" : tab.Title;
                if (title.Length > 16)
                {
                    title = title.Substring(0, 16);
                }

                var grid = new Grid { Width = 168, MaxWidth = 168 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleText = new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 2, 0)
                };
                Grid.SetColumn(titleText, 0);
                grid.Children.Add(titleText);

                var closeBtn = new Button
                {
                    Content = "\uE711",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 10,
                    Width = 22,
                    Height = 22,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "关闭标签页"
                };
                closeBtn.Click += (s, e) => { e.Handled = true; CloseTab(idx); };
                Grid.SetColumn(closeBtn, 1);
                grid.Children.Add(closeBtn);

                var tb = new ToggleButton
                {
                    Style = (Style)FindResource("TabButton"),
                    IsChecked = i == activeTabIndex,
                    Content = grid,
                    Padding = new Thickness(0, 5, 0, 5),
                    Margin = new Thickness(0, 0, 4, 4),
                    Tag = idx
                };
                tb.Click += (s, e) => SwitchTab(idx);
                TabStrip.Children.Add(tb);
            }
        }

        // ================= 视图切换 =================

        private void ShowView(string view)
        {
            HomePanel.Visibility = view == "home" ? Visibility.Visible : Visibility.Collapsed;
            BrowserView.Visibility = view == "browser" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = view == "settings" ? Visibility.Visible : Visibility.Collapsed;
            ExtensionsPanel.Visibility = view == "extensions" ? Visibility.Visible : Visibility.Collapsed;

            NavHomeButton.Tag = view == "home" ? "selected" : null;
            NavSettingsButton.Tag = view == "settings" ? "selected" : null;
            NavExtensionsButton.Tag = view == "extensions" ? "selected" : null;

            // 浏览器视图自动收拢侧边栏，其他视图自动展开。
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
            if (view == "extensions")
            {
                RefreshExtensionsView();
            }
        }

        private void NavHome_Click(object sender, RoutedEventArgs e) => ShowView("home");
        private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowView("settings");
        private void NavExtensions_Click(object sender, RoutedEventArgs e) => ShowView("extensions");

        private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

        private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var dark = !ThemeManager.IsDark;
            ThemeManager.Apply(dark);
            AppSettingsStore.Current.IsDarkMode = dark;
            AppSettingsStore.Save();
            UpdateThemeToggleIcon();
            SetStatus(dark ? "已切换到夜间模式" : "已切换到日间模式");
        }

        private void UpdateThemeToggleIcon()
        {
            // 日间显示月亮（点击切夜间），夜间显示太阳（点击切日间）。
            ThemeToggleButton.Content = ThemeManager.IsDark ? "\uE706" : "\uE708";
        }

        private void ToggleSidebar()
        {
            sidebarCollapsed = !sidebarCollapsed;
            SidebarColumn.Width = new GridLength(sidebarCollapsed ? 56 : 164);
            SidebarHeader.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            NavHomeText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            NavSettingsText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            NavExtensionsText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ToggleSidebarText.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ToggleSidebarIcon.Text = sidebarCollapsed ? "\uE76A" : "\uE76B";
            var pad = sidebarCollapsed ? new Thickness(0, 9, 0, 9) : new Thickness(12, 9, 12, 9);
            var align = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            NavHomeButton.Padding = pad;
            NavSettingsButton.Padding = pad;
            NavExtensionsButton.Padding = pad;
            NavHomeButton.HorizontalContentAlignment = align;
            NavSettingsButton.HorizontalContentAlignment = align;
            NavExtensionsButton.HorizontalContentAlignment = align;
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

        // ================= 浏览器操作 =================

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
            var h = CurrentHost;
            BackButton.IsEnabled = h != null && h.CanGoBack;
            ForwardButton.IsEnabled = h != null && h.CanGoForward;
        }

        private void Navigate(string input)
        {
            NavigateOnTab(activeTabIndex, input);
        }

        private static bool IsUrl(string s)
        {
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return s.Contains(".") && !s.Contains(" ") && !s.Contains("　");
        }

        private void HomeSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenInTab(HomeSearchBox.Text);
                HomeSearchBox.Text = string.Empty;
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

        private void BackButton_Click(object sender, RoutedEventArgs e) => CurrentHost?.Back();
        private void ForwardButton_Click(object sender, RoutedEventArgs e) => CurrentHost?.Forward();
        private void ReloadButton_Click(object sender, RoutedEventArgs e) => CurrentHost?.Reload();
        private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowView("home");

        private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySpeed();
        }

        // ================= 页面缩放（滑块无级 + 按站记忆） =================

        private void ApplyStoredZoom(BrowserTab tab)
        {
            var addr = tab.Host.Address;
            if (string.IsNullOrEmpty(addr) || addr == "about:blank")
            {
                return;
            }
            var percent = ZoomStore.Get(addr);
            if (Math.Abs(percent - 100) > 0.5)
            {
                tab.Host.SetZoomPercent(percent);
            }
        }

        private void ZoomButton_Click(object sender, RoutedEventArgs e)
        {
            var h = CurrentHost;
            if (h == null)
            {
                return;
            }
            var addr = h.Address;
            var current = string.IsNullOrEmpty(addr) || addr == "about:blank"
                ? 100
                : ZoomStore.Get(addr);

            updatingZoomUi = true;
            ZoomSlider.Value = current;
            ZoomValueText.Text = ((int)current) + "%";
            updatingZoomUi = false;
            ZoomPopup.IsOpen = true;
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML 解析设置 Minimum/Maximum 时就会触发本事件，此时构造函数尚未执行、
            // zoomDebounce / ZoomValueText 可能还是 null，必须判空。
            if (ZoomValueText != null)
            {
                ZoomValueText.Text = ((int)e.NewValue) + "%";
            }
            if (updatingZoomUi || zoomDebounce == null)
            {
                return;
            }
            // debounce：拖动停止后才真正应用，避免高频 SetZoomLevel + 磁盘写入卡顿。
            pendingZoomPercent = e.NewValue;
            zoomDebounce.Stop();
            zoomDebounce.Start();
        }

        private void ApplyZoomNow()
        {
            var h = CurrentHost;
            if (h == null)
            {
                return;
            }
            h.SetZoomPercent(pendingZoomPercent);
            var addr = h.Address;
            if (!string.IsNullOrEmpty(addr) && addr != "about:blank")
            {
                ZoomStore.Set(addr, pendingZoomPercent);
            }
        }

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
        {
            updatingZoomUi = true;
            ZoomSlider.Value = 100;
            updatingZoomUi = false;
            ZoomValueText.Text = "100%";

            pendingZoomPercent = 100;
            zoomDebounce.Stop();
            ApplyZoomNow();
        }

        // ================= 收藏 =================

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            var h = CurrentHost;
            if (h == null)
            {
                return;
            }
            var url = h.Address;
            if (string.IsNullOrEmpty(url) || url == "about:blank")
            {
                SetStatus("当前页面无法收藏");
                return;
            }
            if (Favorites.Contains(url))
            {
                Favorites.Remove(url);
                SetStatus("已取消收藏");
            }
            else
            {
                Favorites.Add(url, h.Title ?? url);
                SetStatus("已收藏");
            }
            UpdateFavoriteButton();
        }

        private void UpdateFavoriteButton()
        {
            var h = CurrentHost;
            var fav = h != null && !string.IsNullOrEmpty(h.Address) && Favorites.Contains(h.Address);
            // E735 = 实心星（已收藏），E734 = 空心星（未收藏）。
            FavoriteButton.Content = fav ? "\uE735" : "\uE734";
            FavoriteButton.Foreground = fav
                ? (Brush)FindResource("Theme.Accent")
                : (Brush)FindResource("Theme.TextPrimary");
        }

        private void FavoritesButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new FavoritesWindow();
            win.Owner = this;
            win.NavigateRequested += url => OpenInTab(url);
            win.ShowDialog();
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            CacheManager.ClearCache(() =>
            {
                SetStatus("缓存已清理");
                MessageBox.Show("缓存已清理", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
            });
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
                OpenInTab(item.Url);
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

        // ================= 扩展 =================

        private void RefreshExtensionsView()
        {
            var list = ExtensionsManager.GetInstalled();
            ExtensionList.ItemsSource = list;
            ExtensionEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EdgeStoreButton_Click(object sender, RoutedEventArgs e)
        {
            // 在本软件内部标签页打开扩展商店，不跳转系统默认浏览器。
            OpenInTab(ExtensionsManager.EdgeStoreUrl);
        }

        private void ChromeStoreButton_Click(object sender, RoutedEventArgs e)
        {
            OpenInTab(ExtensionsManager.ChromeStoreUrl);
        }

        private void OpenExtDirButton_Click(object sender, RoutedEventArgs e)
        {
            ExtensionsManager.OpenFolder();
        }

        // ================= 影子（小号） =================

        private void ShadowToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetShadowSidebar(!shadowSidebarExpanded);
        }

        private void SetShadowSidebar(bool expanded)
        {
            shadowSidebarExpanded = expanded;
            ShadowSidebarColumn.Width = new GridLength(expanded ? 220 : 40);
            ShadowContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded)
            {
                RenderShadowList();
            }
        }

        /// <summary>导航到某个网站后检查：若有影子则自动展开右侧边栏。</summary>
        private void CheckShadowAutoExpand()
        {
            var h = CurrentHost;
            var addr = h != null ? h.Address : string.Empty;
            if (string.IsNullOrEmpty(addr) || addr == "about:blank")
            {
                return;
            }
            if (ShadowManager.HasShadowForHost(addr) && !shadowSidebarExpanded)
            {
                SetShadowSidebar(true);
            }
            else if (shadowSidebarExpanded)
            {
                RenderShadowList();
            }
        }

        private void AddShadowButton_Click(object sender, RoutedEventArgs e)
        {
            var h = CurrentHost;
            if (h == null || string.IsNullOrEmpty(h.Address) || h.Address == "about:blank")
            {
                SetStatus("请先打开一个网页再添加影子");
                return;
            }

            var win = new ShadowWindow { Owner = this };
            if (win.ShowDialog() == true)
            {
                var shadow = ShadowManager.Add(win.ShadowName, h.Address, h.Address);
                RenderShadowList();
                SetStatus("已添加影子：" + shadow.Name);
                OpenShadow(shadow);
            }
        }

        private void RenderShadowList()
        {
            ShadowList.Children.Clear();
            var h = CurrentHost;
            var host = h != null ? h.Address : string.Empty;
            var shadows = ShadowManager.GetForHost(host);
            ShadowListTitle.Text = shadows.Count > 0
                ? "已添加的影子（" + shadows.Count + "）"
                : "当前网站暂无影子";

            foreach (var sh in shadows)
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var openBtn = new Button
                {
                    Content = sh.Name,
                    Tag = sh.Id,
                    Height = 32,
                    FontSize = 12,
                    ToolTip = sh.Url
                };
                openBtn.Click += ShadowOpen_Click;
                Grid.SetColumn(openBtn, 0);
                grid.Children.Add(openBtn);

                var delBtn = new Button
                {
                    Content = "\uE74D",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    Tag = sh.Id,
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(4, 0, 0, 0),
                    FontSize = 11,
                    ToolTip = "删除影子"
                };
                delBtn.Click += ShadowDelete_Click;
                Grid.SetColumn(delBtn, 1);
                grid.Children.Add(delBtn);

                ShadowList.Children.Add(grid);
            }
        }

        private void ShadowOpen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var sh = ShadowManager.Items.FirstOrDefault(x => x.Id == id);
                if (sh != null)
                {
                    OpenShadow(sh);
                }
            }
        }

        private void ShadowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is string id))
            {
                return;
            }
            var confirm = MessageBox.Show("删除该影子及其数据？此操作不可恢复。",
                "页游++", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.Yes)
            {
                ShadowManager.Remove(id);
                RenderShadowList();
                SetStatus("已删除影子");
            }
        }

        /// <summary>在新标签页中打开影子（独立 RequestContext，cookie/缓存隔离）。</summary>
        private void OpenShadow(ShadowItem sh)
        {
            try
            {
                var context = ShadowManager.GetContext(sh.Id);
                // 直接以目标 URL + 独立 context 创建标签（CEF 原生导航机制，时序安全）。
                var idx = CreateTab(sh.Url, context);
                activeTabIndex = idx;
                ApplyActiveTab();
                ShowView("browser");
                AddressBox.Text = sh.Url;
            }
            catch (Exception ex)
            {
                SetStatus("打开影子失败：" + ex.Message);
            }
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
                foreach (var f in new[] { "favorites.json", "quicklinks.json", "zoom.json", "shadows.json" })
                {
                    var src = Path.Combine(oldDir, f);
                    if (File.Exists(src))
                    {
                        File.Copy(src, Path.Combine(newPath, f), true);
                    }
                }
                CopyDir(Path.Combine(oldDir, "icons"), Path.Combine(newPath, "icons"));
                CopyDir(Path.Combine(oldDir, "downloads"), Path.Combine(newPath, "downloads"));
                CopyDir(Path.Combine(oldDir, "extensions"), Path.Combine(newPath, "extensions"));
                CopyDir(Path.Combine(oldDir, "profiles"), Path.Combine(newPath, "profiles"));

                AppSettingsStore.Current.DataDirPath = newPath;
                AppSettingsStore.Save();
                AppSettingsStore.WriteDataDirToRegistry(newPath);

                QuickLinks.Reload();
                Favorites.Reload();
                ShadowManager.Reload();
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

            // 检查更新：仅关注稳定版。
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
                UpdateStatusText.Text = "当前已是最新稳定版。";
            }
            else
            {
                UpdateStatusText.Text = "自动检查完成，当前为最新稳定版。";
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
            zoomDebounce?.Stop();
            tray?.Dispose();
            foreach (var tab in tabs)
            {
                tab.Host.Dispose();
            }
            tabs.Clear();
            base.OnClosed(e);
        }
    }
}
