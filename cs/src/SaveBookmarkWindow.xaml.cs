using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 二级收藏弹窗：名称可编辑 + URL 只读 + 三级收藏位置（顶层 / 已有文件夹 / 新建文件夹）。
    /// 当前 URL 已收藏时显示「已收藏」提示与「移除收藏」按钮。
    /// </summary>
    public partial class SaveBookmarkWindow : Window
    {
        private class FolderChoice
        {
            public string Display { get; set; }
            public FavoriteNode Folder { get; set; }
        }

        private readonly string url;

        /// <summary>选中的已有文件夹（null=顶层）。</summary>
        public FavoriteNode SelectedFolder { get; private set; }

        /// <summary>新建文件夹名（选择③时非空）。</summary>
        public string NewFolderName { get; private set; }

        /// <summary>书签名称。</summary>
        public string BookmarkName { get; private set; }

        /// <summary>是否请求移除当前 URL 的收藏。</summary>
        public bool RemoveExisting { get; private set; }

        public SaveBookmarkWindow(string url, string title)
        {
            InitializeComponent();
            this.url = url;
            UrlText.Text = url;
            NameBox.Text = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
            LoadFolders();
            UpdateExistingState();
        }

        private void LoadFolders()
        {
            var options = new List<FolderChoice>();
            foreach (var f in Favorites.GetAllFolders())
            {
                options.Add(new FolderChoice { Display = Favorites.GetFolderPath(f), Folder = f });
            }
            FolderCombo.ItemsSource = options;
            if (options.Count > 0)
            {
                FolderCombo.SelectedIndex = 0;
            }
        }

        private void UpdateExistingState()
        {
            bool exists = Favorites.Contains(url);
            ExistingHint.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
            RemoveButton.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LocRadio_Checked(object sender, RoutedEventArgs e)
        {
            // InitializeComponent 里 TopRadio 的 IsChecked=True 会在 FolderCombo/NewFolderBox
            // 创建之前触发本事件，必须判空。
            if (FolderCombo != null)
            {
                FolderCombo.IsEnabled = ExistingRadio.IsChecked == true;
            }
            if (NewFolderBox != null)
            {
                NewFolderBox.IsEnabled = NewRadio.IsChecked == true;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入书签名称。", "页游++", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (NewRadio.IsChecked == true)
            {
                var folderName = NewFolderBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    MessageBox.Show("请输入新文件夹名称。", "页游++", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                NewFolderName = folderName;
                SelectedFolder = null;
            }
            else if (ExistingRadio.IsChecked == true)
            {
                if (!(FolderCombo.SelectedItem is FolderChoice choice))
                {
                    MessageBox.Show("请选择文件夹。", "页游++", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SelectedFolder = choice.Folder;
                NewFolderName = null;
            }
            else
            {
                SelectedFolder = null;
                NewFolderName = null;
            }

            BookmarkName = name;
            DialogResult = true;
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            RemoveExisting = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
