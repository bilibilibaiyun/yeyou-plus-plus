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
