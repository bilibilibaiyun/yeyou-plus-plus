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
            new ThemeColor("Theme.WindowBg",     "#F2F2F7", "#1B1B1F"),
            new ThemeColor("Theme.SidebarBg",    "#FFFFFF", "#141416"),
            new ThemeColor("Theme.CardBg",       "#FFFFFF", "#26262B"),
            new ThemeColor("Theme.Accent",       "#007AFF", "#2BFF88"),
            new ThemeColor("Theme.TextPrimary",  "#1C1C1E", "#F2F3F7"),
            new ThemeColor("Theme.TextSecondary","#8E8E93", "#9AA0AC"),
            new ThemeColor("Theme.Border",       "#E5E5EA", "#3A3A42"),
            new ThemeColor("Theme.Hover",        "#E9E9EE", "#32323A"),
            new ThemeColor("Theme.Danger",       "#FF3B30", "#FF5C5C"),
            new ThemeColor("Theme.TabBarBg",     "#F2F2F7", "#202024"),
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
