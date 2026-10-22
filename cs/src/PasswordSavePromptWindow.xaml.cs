using System;
using System.Windows;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 保存/更新密码确认窗（模态）。保存（更新）返回 true，否则返回 false。
    /// </summary>
    public partial class PasswordSavePromptWindow : Window
    {
        public PasswordSavePromptWindow(string site, string username, bool isUpdate)
        {
            InitializeComponent();
            var displayUser = string.IsNullOrWhiteSpace(username) ? "(空账号)" : username;
            if (isUpdate)
            {
                Title = "更新密码";
                MessageText.Text = string.Format(
                    "检测到「{0}」在 {1} 的密码已更改。\n\n是否更新已保存的密码？",
                    displayUser, string.IsNullOrWhiteSpace(site) ? "该网站" : site);
                SaveButton.Content = "更新";
                CancelButton.Content = "保留";
            }
            else
            {
                Title = "保存密码";
                MessageText.Text = string.Format(
                    "是否保存「{0}」在 {1} 的登录密码？\n\n密码将用本机 DPAPI 加密存储。",
                    displayUser, string.IsNullOrWhiteSpace(site) ? "该网站" : site);
                SaveButton.Content = "保存";
                CancelButton.Content = "不保存";
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
