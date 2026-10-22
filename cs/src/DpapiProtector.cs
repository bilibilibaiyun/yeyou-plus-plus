using System;
using System.Security.Cryptography;
using System.Text;

namespace YeyouPlusPlus
{
    /// <summary>
    /// DPAPI 加解密：使用当前用户作用域（DataProtectionScope.CurrentUser）、不加固定 entropy。
    /// 明文只在内存中短暂存在，落盘仅保存 Base64 编码的密文。
    /// </summary>
    public static class DpapiProtector
    {
        /// <summary>加密明文，返回 Base64 密文。空串原样返回空串。</summary>
        public static string Protect(string plain)
        {
            if (plain == null)
            {
                return string.Empty;
            }
            var bytes = Encoding.UTF8.GetBytes(plain);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        /// <summary>解密 Base64 密文，返回明文；解密失败返回空串。</summary>
        public static string Unprotect(string base64)
        {
            if (string.IsNullOrEmpty(base64))
            {
                return string.Empty;
            }
            try
            {
                var encrypted = Convert.FromBase64String(base64);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // 换用户/换机或数据损坏时无法解密，返回空串避免崩溃。
                return string.Empty;
            }
        }

        /// <summary>把明文密码掩码为等长的圆点（用于界面展示，避免明文出现）。</summary>
        public static string Mask(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return string.Empty;
            }
            return new string('\u2022', plain.Length);
        }
    }
}
