using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 加密联机码生成 / 解析。
    ///
    /// 联机码内容：本机局域网 IPv4 + TCP 端口(8760) + 6 位随机口令（鉴权 token）。
    ///
    /// 编码方案：
    ///   - 明文 12 字节 = [IPv4 4 字节][端口 2 字节大端][6 位 ASCII 口令]。
    ///   - AES-128-ECB + PKCS7 加密 → 16 字节密文（1 个分组）。
    ///   - 16 字节按 Base62（0-9A-Za-z）编码 → 约 22 位纯字母数字短码。
    ///
    /// 密钥方案：桌面端与手机端使用同一固定常量，经 SHA256 派生前 16 字节作为 AES 密钥。
    ///   （局域网便捷投屏场景，固定密钥 + 每次随机会话口令已满足“防止陌生设备随便连入”的需求；
    ///     口令每次生成不同，密文因此每次不同。）
    /// </summary>
    public sealed class JoinCodeGenerator
    {
        private const string KeySeed = "YeyouPlusPlus.PhoneBox.JoinCode.v1";
        private const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        private static readonly byte[] Key = DeriveKey();

        private string currentToken;
        private string currentIp;
        private int currentPort;
        private string currentNetworkName;
        private string currentNetworkSecret;
        private string currentVirtualIp;

        /// <summary>当前会话口令（手机连接时需在 HelloMessage.Token 中携带）。</summary>
        public string CurrentToken
        {
            get { return currentToken; }
        }

        /// <summary>当前会话的本机局域网 IP。</summary>
        public string CurrentIp
        {
            get { return currentIp; }
        }

        /// <summary>当前会话的 TCP 端口。</summary>
        public int CurrentPort
        {
            get { return currentPort; }
        }

        /// <summary>跨网络会话的网络名（EasyTier 组网标识）。</summary>
        public string CurrentNetworkName
        {
            get { return currentNetworkName; }
        }

        /// <summary>跨网络会话的网络密钥。</summary>
        public string CurrentNetworkSecret
        {
            get { return currentNetworkSecret; }
        }

        /// <summary>跨网络会话的虚拟 IP。</summary>
        public string CurrentVirtualIp
        {
            get { return currentVirtualIp; }
        }

        /// <summary>生成本次会话的联机码，并保存口令 / IP / 端口供后续校验。</summary>
        public string GenerateJoinCode()
        {
            string ip = GetLocalIPv4();
            int port = PhoneBoxConfigStore.Current.TcpPort;
            string token = GenerateToken();

            currentIp = ip;
            currentPort = port;
            currentToken = token;

            byte[] ipBytes = IPAddress.Parse(ip).GetAddressBytes();
            var plaintext = new byte[12];
            Array.Copy(ipBytes, 0, plaintext, 0, 4);
            plaintext[4] = (byte)(port >> 8);
            plaintext[5] = (byte)(port & 0xFF);
            byte[] tokenBytes = Encoding.ASCII.GetBytes(token);
            Array.Copy(tokenBytes, 0, plaintext, 6, 6);

            byte[] cipher = AesEncrypt(plaintext);
            return Base62Encode(cipher);
        }

        /// <summary>
        /// 生成跨网络联机码（EasyTier 组网后使用）。
        /// 明文：虚拟 IPv4(4) + TCP 端口(2) + 网络名(12) + 网络密钥(8) = 26 字节 → AES 32 字节。
        /// 手机端解码后，用网络名/密钥加入同一虚拟网络，再以虚拟 IP 直连。
        /// </summary>
        public string GenerateRemoteJoinCode(string virtualIp, string networkName, string networkSecret)
        {
            int port = PhoneBoxConfigStore.Current.TcpPort;

            currentIp = virtualIp;
            currentPort = port;
            currentToken = networkSecret;
            currentNetworkName = networkName;
            currentNetworkSecret = networkSecret;
            currentVirtualIp = virtualIp;

            byte[] ipBytes = IPAddress.Parse(virtualIp).GetAddressBytes();
            var plaintext = new byte[26];
            Array.Copy(ipBytes, 0, plaintext, 0, 4);
            plaintext[4] = (byte)(port >> 8);
            plaintext[5] = (byte)(port & 0xFF);
            byte[] nameBytes = Encoding.ASCII.GetBytes(networkName.PadRight(12));
            Array.Copy(nameBytes, 0, plaintext, 6, 12);
            byte[] secretBytes = Encoding.ASCII.GetBytes(networkSecret.PadRight(8));
            Array.Copy(secretBytes, 0, plaintext, 18, 8);

            byte[] cipher = AesEncrypt(plaintext);
            return Base62Encode(cipher);
        }

        /// <summary>生成一组随机的 EasyTier 网络名与密钥（供组网与联机码共用）。</summary>
        public static void GenerateNetworkCredentials(out string networkName, out string networkSecret)
        {
            networkName = GenerateNetworkName();
            networkSecret = GenerateSecret();
        }

        /// <summary>校验手机上报的口令是否与当前会话一致。</summary>
        public bool VerifyToken(string token)
        {
            return !string.IsNullOrEmpty(token)
                && !string.IsNullOrEmpty(currentToken)
                && string.Equals(token, currentToken, StringComparison.Ordinal);
        }

        /// <summary>
        /// 解析联机码（供桌面端验证 / 测试；真正解码逻辑在手机端，需与本文档字段布局保持一致）。
        /// </summary>
        public static bool TryDecode(string code, out string ip, out int port, out string token)
        {
            ip = null;
            port = 0;
            token = null;

            try
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    return false;
                }

                byte[] cipher = Base62Decode(code);
                if (cipher == null || cipher.Length != 16)
                {
                    return false;
                }

                byte[] plaintext = AesDecrypt(cipher);
                if (plaintext == null || plaintext.Length != 12)
                {
                    return false;
                }

                ip = new IPAddress(new[] { plaintext[0], plaintext[1], plaintext[2], plaintext[3] }).ToString();
                port = (plaintext[4] << 8) | plaintext[5];
                token = Encoding.ASCII.GetString(plaintext, 6, 6);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析跨网络联机码（供桌面端验证 / 测试；真正解码逻辑在手机端）。
        /// </summary>
        public static bool TryDecodeRemote(
            string code, out string ip, out int port, out string networkName, out string networkSecret)
        {
            ip = null;
            port = 0;
            networkName = null;
            networkSecret = null;

            try
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    return false;
                }

                byte[] cipher = Base62Decode(code);
                if (cipher == null || cipher.Length != 32)
                {
                    return false;
                }

                byte[] plaintext = AesDecrypt(cipher);
                if (plaintext == null || plaintext.Length < 26)
                {
                    return false;
                }

                ip = new IPAddress(new[] { plaintext[0], plaintext[1], plaintext[2], plaintext[3] }).ToString();
                port = (plaintext[4] << 8) | plaintext[5];
                networkName = Encoding.ASCII.GetString(plaintext, 6, 12).Trim();
                networkSecret = Encoding.ASCII.GetString(plaintext, 18, 8).Trim();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] DeriveKey()
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(KeySeed));
                var key = new byte[16];
                Array.Copy(hash, key, key.Length);
                return key;
            }
        }

        private static string GenerateToken()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[4];
                rng.GetBytes(bytes);
                int value = BitConverter.ToInt32(bytes, 0) & 0x7FFFFFFF;
                return (value % 1000000).ToString("D6");
            }
        }

        /// <summary>生成随机的 EasyTier 网络名（固定前缀 + 随机，保证不与其它网络冲突）。</summary>
        private static string GenerateNetworkName()
        {
            return "yypp" + GenerateRandomString(8);
        }

        /// <summary>生成随机的 EasyTier 网络密钥。</summary>
        private static string GenerateSecret()
        {
            return GenerateRandomString(8);
        }

        /// <summary>生成指定长度的随机 Base62 字符串。</summary>
        private static string GenerateRandomString(int length)
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[length];
                rng.GetBytes(bytes);
                var chars = new char[length];
                for (int i = 0; i < length; i++)
                {
                    chars[i] = Base62Alphabet[bytes[i] % 62];
                }
                return new string(chars);
            }
        }

        private static byte[] AesEncrypt(byte[] data)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    return enc.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        private static byte[] AesDecrypt(byte[] data)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = Key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform dec = aes.CreateDecryptor())
                {
                    return dec.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        private static string Base62Encode(byte[] bytes)
        {
            // BigInteger(byte[]) 按小端解释，末尾补 0 作为符号位保证正数。
            var le = new byte[bytes.Length + 1];
            for (int i = 0; i < bytes.Length; i++)
            {
                le[i] = bytes[bytes.Length - 1 - i];
            }
            le[bytes.Length] = 0;

            var value = new BigInteger(le);
            var chars = new List<char>();
            while (value > 0)
            {
                int rem = (int)(value % 62);
                chars.Add(Base62Alphabet[rem]);
                value /= 62;
            }
            chars.Reverse();
            return new string(chars.ToArray());
        }

        private static byte[] Base62Decode(string code)
        {
            var value = BigInteger.Zero;
            foreach (char c in code)
            {
                int idx = Base62Alphabet.IndexOf(c);
                if (idx < 0)
                {
                    return null;
                }
                value = value * 62 + idx;
            }

            byte[] le = value.ToByteArray();
            // 去掉可能的末尾符号字节。
            if (le.Length > 16 && le[le.Length - 1] == 0)
            {
                var trimmed = new byte[le.Length - 1];
                Array.Copy(le, trimmed, trimmed.Length);
                le = trimmed;
            }
            if (le.Length > 16)
            {
                return null;
            }

            var be = new byte[16];
            for (int i = 0; i < le.Length; i++)
            {
                be[15 - i] = le[i];
            }
            return be;
        }

        /// <summary>获取本机局域网 IPv4（优先私有网段，回退任意非回环 IPv4，再回退 127.0.0.1）。</summary>
        private static string GetLocalIPv4()
        {
            string firstNonLoopback = null;

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        string ip = addr.Address.ToString();
                        if (firstNonLoopback == null)
                        {
                            firstNonLoopback = ip;
                        }

                        // 优先返回常见局域网网段（172.x 为简化处理，含非私有的 172 段）。
                        if (ip.StartsWith("192.168.", StringComparison.Ordinal)
                            || ip.StartsWith("10.", StringComparison.Ordinal)
                            || ip.StartsWith("172.", StringComparison.Ordinal))
                        {
                            return ip;
                        }
                    }
                }
            }
            catch
            {
                // 网络枚举失败时走 DNS 回退。
            }

            if (firstNonLoopback != null)
            {
                return firstNonLoopback;
            }

            try
            {
                foreach (var addr in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return addr.ToString();
                    }
                }
            }
            catch
            {
                // 最终回退本机回环地址。
            }

            return "127.0.0.1";
        }
    }
}
