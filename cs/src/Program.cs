using CefSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 应用入口：初始化 CEF + 加载 Flash 插件 + 启动 WPF。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                InitCef();

                var app = new App();
                app.InitializeComponent();
                app.Run();

                if (Cef.IsInitialized)
                {
                    Cef.Shutdown();
                }
            }
            catch (Exception ex)
            {
                // 把异常详情写入日志文件，便于无交互环境下诊断启动失败。
                try
                {
                    File.WriteAllText(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                        ex.ToString());
                }
                catch
                {
                    // 忽略写日志失败。
                }
                MessageBox.Show("启动失败：" + ex.Message, "页游++", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void InitCef()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // Flash 插件从程序目录 plugins 下读取（安装版固定于此）。
            var flashPath = Path.Combine(baseDir, "plugins", "pepflashplayer.dll");

            // 从 Flash 插件的文件版本读取版本号（如 34.0.0.330）。
            string flashVersion = "34.0.0.330";
            try
            {
                if (File.Exists(flashPath))
                {
                    flashVersion = FileVersionInfo.GetVersionInfo(flashPath).FileVersion?.Replace(',', '.') ?? flashVersion;
                }
            }
            catch
            {
                // 使用默认版本号。
            }

            var settings = new CefSharp.WinForms.CefSettings
            {
                CachePath = AppPaths.CacheDir,
                LogFile = Path.Combine(baseDir, "cef_debug.log"),
                LogSeverity = LogSeverity.Verbose,
                // 显式指定子进程路径（与 CefFlashBrowser 一致）。
                BrowserSubprocessPath = Path.Combine(baseDir, "CefSharp.BrowserSubprocess.exe"),
            };

            // 影子（小号）功能依赖：所有 profile 缓存目录的公共根目录。
            // 全局 CachePath 与各影子的 RequestContextSettings.CachePath 都必须是其子目录。
            settings.RootCachePath = AppPaths.ProfilesRoot;

            // [诊断] YPP_DISABLE_GPU=1 时禁用 GPU（排查 GPU 合成内容不呈现问题）。
            if (Environment.GetEnvironmentVariable("YPP_DISABLE_GPU") == "1")
            {
                settings.CefCommandLineArgs["disable-gpu"] = "1";
            }

            // 加载真 Flash 插件（PPAPI）。
            settings.CefCommandLineArgs["ppapi-flash-path"] = flashPath;
            // Chromium 会按版本号判定 Flash「过时」并拒绝加载（黑屏），
            // 必须强行声明一个超大版本号绕过该检查（社区已知解法）。
            settings.CefCommandLineArgs["ppapi-flash-version"] = "99.0.0.999";
            settings.CefCommandLineArgs["enable-system-flash"] = "1";
            settings.CefCommandLineArgs["plugin-policy"] = "allow";
            // 允许过时插件运行 + 关闭「HTML 优先于插件」特性（否则 Flash 被拦截）。
            settings.CefCommandLineArgs["allow-outdated-plugins"] = "1";
            settings.CefCommandLineArgs["disable-features"] = "PreferHtmlOverPlugins";
            // 允许自动播放。
            settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";

            // 加载用户安装的浏览器扩展（每个子目录一个解压后的扩展）。
            var extensionArg = ExtensionsManager.BuildLoadExtensionArg();
            if (!string.IsNullOrEmpty(extensionArg))
            {
                settings.CefCommandLineArgs["load-extension"] = extensionArg;
            }

            // 注意：no-sandbox 参数实测会让 ppapi（Flash）子进程启动即崩溃，
            // CefSharp 本身编译时未启用沙盒（CEF sandbox 未链接），无需此参数。

            if (!Cef.Initialize(settings))
            {
                throw new Exception("CEF 初始化失败");
            }
        }
    }
}
