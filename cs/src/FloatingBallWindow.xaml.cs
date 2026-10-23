using System;
using System.Windows;
using System.Windows.Threading;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 右下角悬浮球（独立置顶 HWND，复用密码填充浮层的无边框置顶窗口范式）。
    ///
    /// 锚定主窗口右下角约 20px；点击展开「进入 / 退出全屏沉浸式浏览」菜单；
    /// 沉浸式期间菜单内显示「按 Esc 退出」提示。窗口自身由主窗口负责
    /// 移动 / 缩放 / 最大化联动与生命周期管理。
    /// </summary>
    public partial class FloatingBallWindow : Window
    {
        /// <summary>用户点击「进入 / 退出全屏沉浸式浏览」。</summary>
        public event Action ImmersiveToggleRequested;

        private bool immersive;

        public FloatingBallWindow()
        {
            InitializeComponent();
        }

        /// <summary>同步沉浸式状态：切换按钮文案与 Esc 提示可见性。</summary>
        public void SetImmersive(bool value)
        {
            immersive = value;
            ImmersiveToggleButton.Content = value ? "退出全屏沉浸式浏览" : "进入全屏沉浸式浏览";
            EscHintText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>以主窗口右下角为锚点重新定位（距右 / 下边约 20px）。</summary>
        public void Reposition()
        {
            if (Owner == null)
            {
                return;
            }

            const double margin = 20;
            double right;
            double bottom;

            if (Owner.WindowState == WindowState.Maximized)
            {
                // 最大化时窗口边框会溢出屏幕（Owner.Left/Top 出现约 -8px 负偏移），
                // 直接累加 ActualWidth/Height 会让悬浮球距可视边缘只有约 12px。
                // 改用内容区右下角的屏幕坐标（DIP）作为锚点，得到真实可视边缘。
                Point anchor = Owner.PointToScreen(new Point(Owner.RenderSize.Width, Owner.RenderSize.Height));
                right = anchor.X - margin;
                bottom = anchor.Y - margin;
            }
            else
            {
                right = Owner.Left + Owner.ActualWidth - margin;
                bottom = Owner.Top + Owner.ActualHeight - margin;
            }

            // 以窗口右下角为锚点：菜单展开窗口变高时向上生长，悬浮球始终贴住右下角。
            Left = right - ActualWidth;
            Top = bottom - ActualHeight;
        }

        /// <summary>显示悬浮球（首次显示收起菜单并定位）。</summary>
        public void ShowBall()
        {
            if (!IsVisible)
            {
                MenuPanel.Visibility = Visibility.Collapsed;
                Show();
            }
            Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Loaded);
        }

        /// <summary>隐藏悬浮球。</summary>
        public void HideBall()
        {
            Hide();
        }

        private void BallButton_Click(object sender, RoutedEventArgs e)
        {
            MenuPanel.Visibility = MenuPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Loaded);
        }

        private void ImmersiveToggleButton_Click(object sender, RoutedEventArgs e)
        {
            MenuPanel.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Loaded);
            ImmersiveToggleRequested?.Invoke();
        }
    }
}
