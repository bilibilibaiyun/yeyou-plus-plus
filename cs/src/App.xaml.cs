using System.Windows;

namespace YeyouPlusPlus
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // [二分实验A] 暂时禁用启动时主题应用，排查 CEF 显示空白问题。
            // ThemeManager.Apply(AppSettingsStore.Current.IsDarkMode);
        }
    }
}
