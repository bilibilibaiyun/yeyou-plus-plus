using System.Windows;

namespace YeyouPlusPlus
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // 启动时按设置应用主题（默认日间）。
            ThemeManager.Apply(AppSettingsStore.Current.IsDarkMode);
        }
    }
}
