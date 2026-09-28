using System.Windows;

namespace YeyouPlusPlus
{
    public partial class ShadowWindow : Window
    {
        /// <summary>用户输入的备注名称（可为空）。</summary>
        public string ShadowName { get; private set; }

        public ShadowWindow()
        {
            InitializeComponent();
            NameBox.Focus();
        }

        /// <summary>以「重命名」模式打开：预填当前名称、改为重命名文案。</summary>
        public ShadowWindow(string currentName)
        {
            InitializeComponent();
            Title = "重命名影子";
            TitleText.Text = "修改该影子的备注名称";
            DescText.Text = "仅修改备注名称，影子的登录状态和数据不受影响。";
            LabelText.Text = "备注名称";
            ConfirmButton.Content = "保存";
            NameBox.Text = currentName ?? string.Empty;
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            ShadowName = NameBox.Text == null ? string.Empty : NameBox.Text.Trim();
            DialogResult = true;
        }
    }
}
