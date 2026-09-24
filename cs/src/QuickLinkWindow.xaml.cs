using System;
using System.Windows;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 添加快捷入口的二级窗口。
    /// </summary>
    public partial class QuickLinkWindow : Window
    {
        /// <summary>保存后的地址。</summary>
        public string Url { get; private set; }

        /// <summary>保存后的名称（可为空）。</summary>
        public string LinkName { get; private set; }

        public QuickLinkWindow()
        {
            InitializeComponent();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var url = AddressBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("请输入网址", "页游++", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Url = url;
            LinkName = NameBox.Text.Trim();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
