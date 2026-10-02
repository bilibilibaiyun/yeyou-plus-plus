using CefSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 应用入口：初始化 CEF + 加载 Flash 插件 + 启动 WPF。
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// 设置系统定时器最小分辨率（1ms）。
        /// 必须在 Flash 初始化之前调用，使 Flash 判定 timeGetTime 精度足够并选为主时间源，
        /// 从而让 speedhack 的 timeGetTime hook 真正驱动游戏变速（参考 CN104636138B）。
        /// 该设置是系统级（winmm）调用，返回 TIMERR_NOERROR(0) 表示成功。
        /// </summary>
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // 覆盖安装后恢复备份的用户数据（账号/收藏/配置），必须在 CEF 初始化前完成。
                DataBackup.RestoreIfNeeded();

                // 在 InitCef（进而启动 CEF 子进程/Flash 插件进程）之前把系统定时器分辨率提到 1ms。
                // timeBeginPeriod 是系统级设置，主进程启动即调用，保证所有 CEF 子进程
                // 在 Flash 初始化前系统定时器精度已是 1ms。
                timeBeginPeriod(1);

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
                // 伪装成较新 Chrome：内核虽为 Chromium 84，但现代人机验证服务（极验/腾讯防水墙等）
                // 会按 UA 判定浏览器过旧并拒绝渲染验证码图案。CefSettingsBase.UserAgent 直接
                // 覆盖默认 UA（等价于 Chromium 的 --user-agent），社区通用做法。不含 CefSharp/CEF 标识。
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                // Accept-Language 设为中文优先，避免部分国内验证码服务因默认 en-US 拒绝出图。
                AcceptLanguageList = "zh-CN,zh;q=0.9,en;q=0.8",
            };

            // 影子功能依赖：所有 profile 缓存目录的公共根目录。
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

            // 注意：no-sandbox 参数实测会让 ppapi（Flash）子进程启动即崩溃，
            // CefSharp 本身编译时未启用沙盒（CEF sandbox 未链接），无需此参数。

            if (!Cef.Initialize(settings))
            {
                throw new Exception("CEF 初始化失败");
            }
        }
    }
}
