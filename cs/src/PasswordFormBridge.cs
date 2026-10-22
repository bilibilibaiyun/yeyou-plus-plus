using System;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 每个标签页一个实例，注册为 JS 桥接对象「yppBridge」（isAsync: true）。
    /// CEF 回调线程收到 JS 调用后切回 UI 线程，再转发给所属 BrowserHost 的事件。
    /// </summary>
    public class PasswordFormBridge
    {
        private readonly BrowserHost _host;

        public PasswordFormBridge(BrowserHost host)
        {
            _host = host;
        }

        /// <summary>JS 上报登录表单提交（username, password, origin, actionUrl）。</summary>
        public void LoginCaptured(string username, string password, string origin, string actionUrl)
        {
            _host.Dispatcher.InvokeAsync(() =>
                _host.RaiseLoginCaptured(username, password, origin, actionUrl));
        }

        /// <summary>JS 上报输入框聚焦（origin, rectJson）。</summary>
        public void LoginFieldFocused(string origin, string rectJson)
        {
            _host.Dispatcher.InvokeAsync(() =>
                _host.RaiseLoginFieldFocused(origin, rectJson));
        }

        /// <summary>把某条密码记录回填到当前标签页（解密后经 ExecuteJavaScriptAsync 注入）。</summary>
        public void Fill(PasswordEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            var plain = PasswordManager.Decrypt(entry.PasswordEncrypted);
            _host.Dispatcher.InvokeAsync(() =>
                _host.FillPassword(entry.Username, plain));
        }
    }
}
