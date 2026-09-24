using System;
using System.Windows;
using System.Windows.Input;

namespace YeyouPlusPlus
{
    public partial class FavoritesWindow : Window
    {
        /// <summary>请求导航到指定 URL。</summary>
        public event Action<string> NavigateRequested;

        public FavoritesWindow()
        {
            InitializeComponent();
            FavoriteList.ItemsSource = Favorites.Items;
        }

        private void FavoriteList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FavoriteList.SelectedItem is FavoriteItem item)
            {
                NavigateRequested?.Invoke(item.Url);
                Close();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (FavoriteList.SelectedItem is FavoriteItem item)
            {
                Favorites.Remove(item.Url);
                FavoriteList.ItemsSource = null;
                FavoriteList.ItemsSource = Favorites.Items;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
