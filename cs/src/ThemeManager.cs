using System.Windows;
using System.Windows.Media;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 主题管理：日间（白色 + 淡蓝）/ 夜间（黑色 + 荧光绿）。
    /// 通过替换 Application.Resources 里的 Theme.* 画刷实现，所有
    /// 界面均以 DynamicResource 引用，替换后即时刷新。
    /// </summary>
    public static class ThemeManager
    {
        private sealed class ThemeColor
        {
            public readonly string Key;
            public readonly string Light;
            public readonly string Dark;

            public ThemeColor(string key, string light, string dark)
            {
                Key = key;
                Light = light;
                Dark = dark;
            }
        }

        private static readonly ThemeColor[] ThemeColors =
        {
            new ThemeColor("Theme.WindowBg",     "#F5F6FA", "#1B1B1F"),
            new ThemeColor("Theme.SidebarBg",    "#EBEDF3", "#141416"),
            new ThemeColor("Theme.CardBg",       "#FFFFFF", "#26262B"),
            new ThemeColor("Theme.Accent",       "#3B7BEB", "#2BFF88"),
            new ThemeColor("Theme.TextPrimary",  "#1A1C24", "#F2F3F7"),
            new ThemeColor("Theme.TextSecondary","#6A6E7C", "#9AA0AC"),
            new ThemeColor("Theme.Border",       "#D9DCE3", "#3A3A42"),
            new ThemeColor("Theme.Hover",        "#E3E6ED", "#32323A"),
            new ThemeColor("Theme.Danger",       "#E5484D", "#FF5C5C"),
            new ThemeColor("Theme.TabBarBg",     "#E8EAF0", "#202024"),
        };

        /// <summary>当前是否为夜间模式。</summary>
        public static bool IsDark { get; private set; }

        /// <summary>应用主题（dark=true 夜间，false 日间）。</summary>
        public static void Apply(bool dark)
        {
            IsDark = dark;
            var res = Application.Current.Resources;
            foreach (var tc in ThemeColors)
            {
                var color = (Color)ColorConverter.ConvertFromString(dark ? tc.Dark : tc.Light);
                res[tc.Key] = new SolidColorBrush(color);
            }
        }
    }
}
