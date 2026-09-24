using System;
using System.Drawing;
using System.Windows.Forms;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 系统托盘图标：显示主窗口 / 退出。图标取自 exe 内嵌资源。
    /// </summary>
    public class TrayService : IDisposable
    {
        private readonly NotifyIcon icon;

        public TrayService(Action showMain, Action exitApp)
        {
            try
            {
                icon = new NotifyIcon
                {
                    Text = "页游++",
                    Visible = true
                };
                try
                {
                    icon.Icon = Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
                }
                catch
                {
                    // 取不到 exe 图标就保持默认。
                }

                var menu = new ContextMenuStrip();
                menu.Items.Add("显示主窗口", null, (s, e) => { try { showMain(); } catch { } });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, (s, e) => { try { exitApp(); } catch { } });
                icon.ContextMenuStrip = menu;
                icon.DoubleClick += (s, e) => { try { showMain(); } catch { } };
            }
            catch
            {
                // 托盘初始化失败不影响主程序。
            }
        }

        public void Dispose()
        {
            try
            {
                if (icon != null)
                {
                    icon.Visible = false;
                    icon.Dispose();
                }
            }
            catch
            {
                // 忽略。
            }
        }
    }
}
