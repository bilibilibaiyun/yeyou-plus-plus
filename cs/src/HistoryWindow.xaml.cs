using System;
using System.Windows;
using System.Windows.Input;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 浏览历史弹窗：搜索 + 倒序列表 + 单删 + 清空；双击打开对应页面。
    /// </summary>
    public partial class HistoryWindow : Window
    {
        /// <summary>请求打开指定 URL。</summary>
        public event Action<string> NavigateRequested;

        public HistoryWindow()
        {
            InitializeComponent();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            HistoryList.ItemsSource = HistoryService.Search(SearchBox.Text);
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HistoryList.SelectedItem is HistoryItem item)
            {
                NavigateRequested?.Invoke(item.Url);
                Close();
            }
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is HistoryItem item)
            {
                HistoryService.Remove(item.Url);
                ApplyFilter();
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show("确定清空全部浏览历史吗？此操作不可恢复。",
                "页游++", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.Yes)
            {
                HistoryService.Clear();
                ApplyFilter();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
