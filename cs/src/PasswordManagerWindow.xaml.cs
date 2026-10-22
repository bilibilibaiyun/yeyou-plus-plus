using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 密码管理页：列出已存密码（站点/账号/掩码），支持显示明文、编辑、删除。
    /// </summary>
    public partial class PasswordManagerWindow : Window
    {
        public PasswordManagerWindow()
        {
            InitializeComponent();
            RefreshList();
        }

        private void RefreshList()
        {
            var items = PasswordManager.Items;
            PasswordList.ItemsSource = null;
            PasswordList.ItemsSource = items;
            TitleText.Text = items.Count > 0
                ? "已保存的密码（" + items.Count + "）"
                : "已保存的密码（暂无）";
        }

        private void ShowPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!(PasswordList.SelectedItem is PasswordEntry entry))
            {
                MessageBox.Show("请先选择一条记录。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var plain = PasswordManager.Decrypt(entry.PasswordEncrypted);
            if (string.IsNullOrEmpty(plain))
            {
                MessageBox.Show("密码解密失败（可能换用户/换机或数据损坏）。", "页游++",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBox.Show(
                "站点：" + entry.Site + "\n账号：" + entry.Username + "\n密码：" + plain + "\n\n请勿泄露给他人。",
                "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (!(PasswordList.SelectedItem is PasswordEntry entry))
            {
                MessageBox.Show("请先选择一条记录。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (ShowEditDialog(entry, out string username, out string password))
            {
                PasswordManager.Update(entry.Site, entry.Username, username, password);
                RefreshList();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!(PasswordList.SelectedItem is PasswordEntry entry))
            {
                MessageBox.Show("请先选择一条记录。", "页游++", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(
                "确定删除「" + entry.Site + " / " + entry.Username + "」吗？此操作不可恢复。",
                "页游++", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.Yes)
            {
                PasswordManager.Delete(entry.Site, entry.Username);
                RefreshList();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>编辑账号/密码的简易对话框（站点为唯一键，不可编辑）。</summary>
        private bool ShowEditDialog(PasswordEntry entry, out string username, out string password)
        {
            string newUsername = entry.Username;
            string newPassword = string.Empty;

            var secondary = (Brush)FindResource("Theme.TextSecondary");
            var windowBg = (Brush)FindResource("Theme.WindowBg");
            var textPrimary = (Brush)FindResource("Theme.TextPrimary");

            var win = new Window
            {
                Title = "编辑密码",
                Width = 400,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = windowBg,
                Foreground = textPrimary
            };

            var stack = new StackPanel { Margin = new Thickness(18) };

            stack.Children.Add(new TextBlock { Text = "站点", FontSize = 12, Foreground = secondary });
            var siteBox = new TextBox
            {
                Text = entry.Site,
                IsReadOnly = true,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(siteBox);

            stack.Children.Add(new TextBlock
            {
                Text = "账号",
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = secondary
            });
            var userBox = new TextBox
            {
                Text = entry.Username,
                Height = 32,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(userBox);

            stack.Children.Add(new TextBlock
            {
                Text = "密码",
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = secondary
            });
            var pwdBox = new PasswordBox { Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            stack.Children.Add(pwdBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancelBtn = new Button { Content = "取消", Width = 80, Height = 32 };
            cancelBtn.Click += (s, e) => win.DialogResult = false;
            var saveBtn = new Button
            {
                Content = "保存",
                Width = 80,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("PrimaryButton")
            };
            saveBtn.Click += (s, e) =>
            {
                var p = pwdBox.Password;
                if (string.IsNullOrEmpty(p))
                {
                    MessageBox.Show("密码不能为空。", "页游++", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                newPassword = p;
                newUsername = userBox.Text.Trim();
                win.DialogResult = true;
            };
            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(saveBtn);
            stack.Children.Add(btnRow);

            win.Content = stack;
            var ok = win.ShowDialog() == true;
            username = newUsername;
            password = newPassword;
            return ok;
        }
    }
}
