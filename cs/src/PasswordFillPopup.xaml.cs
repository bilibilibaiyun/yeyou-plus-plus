using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 点击后填充的候选账号浮层：无边框、置顶、不抢焦点（ShowActivated=false）、仅填充不提交。
    /// </summary>
    public partial class PasswordFillPopup : Window
    {
        /// <summary>用户点选某条密码记录。</summary>
        public event Action<PasswordEntry> EntrySelected;

        private bool _activatedOnce;

        public PasswordFillPopup()
        {
            InitializeComponent();

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Hide();
                }
            };

            // 浮层被点击激活过一次后，再失焦（点击别处）即自动隐藏。
            Activated += (s, e) => _activatedOnce = true;
            Deactivated += (s, e) =>
            {
                if (_activatedOnce)
                {
                    Hide();
                    _activatedOnce = false;
                }
            };
        }

        /// <summary>在指定屏幕坐标（DIP）展示候选账号，并夹紧到工作区内。</summary>
        public void ShowAt(double screenX, double screenY, IReadOnlyList<PasswordEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                Hide();
                return;
            }

            AccountList.ItemsSource = new List<PasswordEntry>(entries);
            AccountList.SelectedIndex = -1;

            Left = screenX;
            Top = screenY;
            if (!IsVisible)
            {
                Show();
            }
            UpdateLayout();
            ClampToWorkArea();
        }

        private void ClampToWorkArea()
        {
            var wa = SystemParameters.WorkArea;
            if (ActualWidth > 0 && Left + ActualWidth > wa.Right)
            {
                Left = wa.Right - ActualWidth;
            }
            if (ActualHeight > 0 && Top + ActualHeight > wa.Bottom)
            {
                Top = wa.Bottom - ActualHeight;
            }
            if (Left < wa.Left)
            {
                Left = wa.Left;
            }
            if (Top < wa.Top)
            {
                Top = wa.Top;
            }
        }

        private void AccountList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (AccountList.SelectedItem is PasswordEntry entry)
            {
                Hide();
                EntrySelected?.Invoke(entry);
            }
        }
    }
}
