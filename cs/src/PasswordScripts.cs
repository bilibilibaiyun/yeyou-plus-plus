using Newtonsoft.Json;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 密码功能注入/回填 JS 脚本。
    /// 注入脚本幂等（window.__yppPwdInjected 防重复挂监听），负责：
    ///   1) 捕获表单 submit 并启发式提取 username/password，经 yppBridge.LoginCaptured 上报；
    ///   2) 监听 focusin，用户名/密码输入框聚焦时上报 origin + 坐标（供定位填充浮层），
    ///      并记住最后聚焦元素（window.__yppLastFocused）供回填脚本使用。
    /// 回填脚本把账号/密码写回对应输入框并派发 input/change，不自动提交。
    /// </summary>
    public static class PasswordScripts
    {
        public const string Inject = @"
(function () {
  if (window.__yppPwdInjected) return;
  window.__yppPwdInjected = true;

  function pickUsername(form, pwd) {
    var email = form.querySelector ? form.querySelector('input[type=email]') : null;
    if (email) return email;
    var candidates = form.querySelectorAll ? form.querySelectorAll('input') : [];
    for (var i = 0; i < candidates.length; i++) {
      var el = candidates[i];
      if (el === pwd || el.type === 'password') continue;
      var hay = ((el.name || '') + ' ' + (el.id || '') + ' ' + (el.autocomplete || '') + ' ' + (el.type || '')).toLowerCase();
      if (/(user|login|account|phone|mail|id|email|mobile)/.test(hay)) return el;
    }
    for (var j = 0; j < candidates.length; j++) {
      if (candidates[j] === pwd) {
        for (var k = j - 1; k >= 0; k--) {
          var t = candidates[k].type;
          if (t !== 'password') return candidates[k];
        }
      }
    }
    return null;
  }

  // 上报去抖：submit / click / Enter 可能接连触发同样的登录，1.5 秒内同账号+密码只报一次。
  var lastReportU = null, lastReportP = null, lastReportT = 0;
  function report(u, p, origin, action) {
    var now = Date.now();
    if (u === lastReportU && p === lastReportP && (now - lastReportT) < 1500) return;
    lastReportU = u; lastReportP = p; lastReportT = now;
    if (window.yppBridge && window.yppBridge.LoginCaptured) {
      window.yppBridge.LoginCaptured(u, p, origin, action);
    }
  }

  function reportForm(form) {
    try {
      if (!form || typeof form.querySelectorAll !== 'function') return;
      var pwds = form.querySelectorAll('input[type=password]');
      if (!pwds.length) return;
      var pwd = pwds[0];
      var p = pwd.value;
      if (!p) return;
      var uEl = pickUsername(form, pwd);
      var u = uEl ? uEl.value : '';
      var origin = window.location.origin || (window.location.protocol + '//' + window.location.host);
      var action = (form.action && form.action !== '') ? form.action : window.location.href;
      report(u, p, origin, action);
    } catch (err) {}
  }

  // 捕获 1：原生 form submit 提交。
  document.addEventListener('submit', function (e) {
    reportForm(e.target);
  }, true);

  // 捕获 2：登录按钮点击（大量网站用 AJAX 登录，不触发 submit；点击时兜底提取）。
  document.addEventListener('click', function (e) {
    try {
      var t = e.target;
      if (!t || !t.tagName) return;
      var tn = t.tagName.toLowerCase();
      var isBtn = (tn === 'button') ||
        (tn === 'input' && (t.type === 'button' || t.type === 'submit' || t.type === 'image')) ||
        (tn === 'a') ||
        (t.getAttribute && t.getAttribute('role') === 'button');
      if (!isBtn) return;
      var form = (t.closest && t.closest('form')) || null;
      if (form) {
        reportForm(form);
        return;
      }
      // 无 form（纯 AJAX 登录）：全文档找已填密码框。
      var pwd = document.querySelector('input[type=password]');
      if (!pwd || !pwd.value) return;
      var origin = window.location.origin || (window.location.protocol + '//' + window.location.host);
      var uEl = pickUsername(pwd.form || document, pwd);
      report(uEl ? uEl.value : '', pwd.value, origin, window.location.href);
    } catch (err) {}
  }, true);

  // 捕获 3：密码框内按 Enter（AJAX 登录常用回车提交）。
  document.addEventListener('keydown', function (e) {
    try {
      if (e.key !== 'Enter') return;
      var el = document.activeElement;
      if (!el || el.type !== 'password' || !el.value) return;
      var form = el.form || document;
      var pwds = form.querySelectorAll ? form.querySelectorAll('input[type=password]') : [];
      if (!pwds.length) return;
      var uEl = pickUsername(form, el);
      var origin = window.location.origin || (window.location.protocol + '//' + window.location.host);
      var action = (form.action && form.action !== '') ? form.action : window.location.href;
      report(uEl ? uEl.value : '', el.value, origin, action);
    } catch (err) {}
  }, true);

  function reportRect(el) {
    try {
      var r = el.getBoundingClientRect();
      var origin = window.location.origin || (window.location.protocol + '//' + window.location.host);
      if (window.yppBridge && window.yppBridge.LoginFieldFocused) {
        window.yppBridge.LoginFieldFocused(origin, JSON.stringify({ x: r.left, y: r.top, w: r.width, h: r.height }));
      }
    } catch (err) {}
  }

  function reportFocus(e) {
    try {
      var el = e.target;
      if (!el || !el.tagName) return;
      var tn = el.tagName.toLowerCase();
      if (tn !== 'input' && tn !== 'textarea') return;
      if (el.type === 'password') {
        window.__yppLastFocused = el;
        reportRect(el);
        return;
      }
      var form = el.form || document;
      var pwds = (form.querySelectorAll ? form.querySelectorAll('input[type=password]') : []);
      var hay = ((el.name || '') + ' ' + (el.id || '') + ' ' + (el.autocomplete || '') + ' ' + (el.type || '')).toLowerCase();
      if (pwds.length && /(user|login|account|phone|mail|id|email|mobile|name)/.test(hay)) {
        window.__yppLastFocused = el;
        reportRect(el);
      }
    } catch (err) {}
  }

  document.addEventListener('focusin', reportFocus, true);

  (async function () {
    try {
      if (window.CefSharp && window.CefSharp.BindObjectAsync) {
        await window.CefSharp.BindObjectAsync('yppBridge');
      }
    } catch (err) {}
  })();
})();
";

        /// <summary>生成回填账号/密码的 JS（含 JSON 转义，避免注入破坏脚本）。</summary>
        public static string BuildFillScript(string username, string password)
        {
            var u = JsonConvert.SerializeObject(username ?? string.Empty);
            var p = JsonConvert.SerializeObject(password ?? string.Empty);

            var script = @"
(function () {
  var u = __USERNAME__;
  var p = __PASSWORD__;

  function setValue(el, v) {
    if (!el) return false;
    try {
      var proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
      var desc = Object.getOwnPropertyDescriptor(proto, 'value');
      if (desc && desc.set) desc.set.call(el, v); else el.value = v;
    } catch (e) {
      el.value = v;
    }
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  }

  var host = window.__yppLastFocused;
  var form = (host && host.form) ? host.form : document;
  var pwds = form.querySelectorAll ? form.querySelectorAll('input[type=password]') : [];
  var pwd = pwds.length ? pwds[0] : null;
  var inputs = form.querySelectorAll ? form.querySelectorAll('input') : [];
  var uEl = null;

  for (var i = 0; i < inputs.length; i++) {
    if (inputs[i].type === 'email') { uEl = inputs[i]; break; }
  }
  if (!uEl) {
    for (var j = 0; j < inputs.length; j++) {
      var el2 = inputs[j];
      if (el2 === pwd || el2.type === 'password') continue;
      var hay = ((el2.name || '') + ' ' + (el2.id || '') + ' ' + (el2.autocomplete || '') + ' ' + (el2.type || '')).toLowerCase();
      if (/(user|login|account|phone|mail|id|email|mobile)/.test(hay)) { uEl = el2; break; }
    }
  }
  if (!uEl) {
    for (var k = 0; k < inputs.length; k++) {
      if (inputs[k] === pwd) {
        for (var m = k - 1; m >= 0; m--) {
          if (inputs[m].type !== 'password') { uEl = inputs[m]; break; }
        }
        break;
      }
    }
  }

  if (u) setValue(uEl, u);
  if (p) setValue(pwd, p);
})();
";

            return script.Replace("__USERNAME__", u).Replace("__PASSWORD__", p);
        }
    }
}
