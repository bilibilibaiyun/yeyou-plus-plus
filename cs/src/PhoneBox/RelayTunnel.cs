using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 中继服务器隧道客户端。
    ///
    /// 中继服务器协议（纯文本行 + '\n'）：
    ///   - 主设备：连 9000，发 "HOST\n" → 收 "CODE &lt;8位数字&gt;\n"（进入等待副设备）。
    ///   - 副设备：连 9000，发 "JOIN &lt;8位数字&gt;\n" → 收 "LINKED\n"（配对成功）。
    ///   - 配对成功后，服务器在两端之间透明双向转发字节流（不解析内容）。
    ///
    /// 隧道帧格式（配对成功后，主副在隧道上通信，多路复用控制通道和视频）：
    ///   每帧 = [1 字节类型][4 字节大端长度][数据]。
    /// </summary>
    public sealed class RelayTunnel : IDisposable
    {
        /// <summary>中继服务器默认地址。</summary>
        public const string DefaultServerHost = "43.226.36.117";

        /// <summary>中继服务器默认端口。</summary>
        public const int DefaultServerPort = 9000;

        /// <summary>控制消息帧类型（JSON）。</summary>
        public const byte FrameTypeControl = 0x01;

        /// <summary>视频帧类型（H.264 Annex-B 完整帧）。</summary>
        public const byte FrameTypeVideo = 0x02;

        private const int ConnectTimeoutMs = 10000;
        private const int MaxFrameSize = 64 * 1024 * 1024;

        private readonly string serverHost;
        private readonly int serverPort;
        private readonly object sync = new object();

        private TcpClient tcpClient;
        private NetworkStream stream;
        private bool connected;
        private bool disposed;

        public RelayTunnel()
            : this(DefaultServerHost, DefaultServerPort)
        {
        }

        public RelayTunnel(string serverHost, int serverPort)
        {
            if (string.IsNullOrWhiteSpace(serverHost))
            {
                throw new ArgumentNullException(nameof(serverHost));
            }
            if (serverPort <= 0 || serverPort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(serverPort));
            }

            this.serverHost = serverHost;
            this.serverPort = serverPort;
        }

        /// <summary>中继服务器地址。</summary>
        public string ServerHost
        {
            get { return serverHost; }
        }

        /// <summary>中继服务器端口。</summary>
        public int ServerPort
        {
            get { return serverPort; }
        }

        /// <summary>是否已连接中继服务器。</summary>
        public bool IsConnected
        {
            get
            {
                lock (sync)
                {
                    return connected && tcpClient != null && tcpClient.Connected;
                }
            }
        }

        /// <summary>连接中继服务器。</summary>
        public void Connect()
        {
            lock (sync)
            {
                if (connected)
                {
                    return;
                }
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(RelayTunnel));
                }

                var client = new TcpClient { NoDelay = true };
                var ar = client.BeginConnect(serverHost, serverPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    client.Close();
                    throw new IOException("连接中继服务器超时。");
                }
                client.EndConnect(ar);

                tcpClient = client;
                stream = client.GetStream();
                connected = true;
            }

            PhoneBoxLog.Info("中继服务器已连接：" + serverHost + ":" + serverPort + "。");
        }

        /// <summary>
        /// 以主机身份登记：发送 "HOST" 行并读回 "CODE &lt;8位数字&gt;"，返回 8 位联机码。
        /// </summary>
        public string SendHost()
        {
            Connect();
            WriteRawLine("HOST");
            string line = ReadRawLine();
            if (string.IsNullOrEmpty(line))
            {
                throw new IOException("中继服务器未返回联机码。");
            }

            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !string.Equals(parts[0], "CODE", StringComparison.OrdinalIgnoreCase)
                || parts[1].Length != 8)
            {
                throw new IOException("中继服务器返回异常：" + line);
            }

            foreach (char c in parts[1])
            {
                if (c < '0' || c > '9')
                {
                    throw new IOException("中继服务器返回异常：" + line);
                }
            }

            return parts[1];
        }

        /// <summary>
        /// 以副设备身份加入：发送 "JOIN &lt;code&gt;" 行并读回响应。
        /// "LINKED" 表示配对成功；"NOTFOUND" / "BUSY" 或异常响应时抛异常（信息友好）。
        /// </summary>
        public void Join(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentNullException(nameof(code));
            }

            Connect();
            WriteRawLine("JOIN " + code);
            string line = ReadRawLine();

            if (string.Equals(line, "LINKED", StringComparison.OrdinalIgnoreCase))
            {
                PhoneBoxLog.Info("中继配对成功（联机码 " + code + "）。");
                return;
            }

            // 配对失败：关闭连接以便下次重试。
            Close();

            if (string.IsNullOrEmpty(line))
            {
                throw new IOException("中继服务器未响应。");
            }
            if (string.Equals(line, "NOTFOUND", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("未找到该联机码对应的主机，请确认联机码正确且主机已生成。");
            }
            if (string.Equals(line, "BUSY", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("该主机已被其他设备连接。");
            }

            throw new IOException("中继服务器返回异常：" + line);
        }

        /// <summary>
        /// 写入一帧隧道数据：[1 字节类型][4 字节大端长度][数据]。写操作串行化。
        /// </summary>
        public void WriteFrame(byte type, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            lock (sync)
            {
                EnsureConnected();
                stream.WriteByte(type);
                WriteInt32Be(stream, data.Length);
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }

        /// <summary>
        /// 读取一帧隧道数据。连接断开 / 读失败返回 false（type=0、data=null）。
        /// </summary>
        public bool ReadFrame(out byte type, out byte[] data)
        {
            type = 0;
            data = null;

            NetworkStream s;
            lock (sync)
            {
                s = stream;
            }
            if (s == null)
            {
                return false;
            }

            try
            {
                int t = s.ReadByte();
                if (t < 0)
                {
                    return false;
                }
                type = (byte)t;

                int length = ReadInt32Be(s);
                if (length < 0 || length > MaxFrameSize)
                {
                    PhoneBoxLog.Warn("中继隧道收到非法帧长度：" + length + "。");
                    return false;
                }

                data = new byte[length];
                ReadExactly(s, data, 0, length);
                return true;
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Warn("中继隧道读取失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>发送一行控制消息（内部封装为 type=0x01 帧）。</summary>
        public void WriteLine(string line)
        {
            if (line == null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            byte[] data = Encoding.UTF8.GetBytes(line);
            WriteFrame(FrameTypeControl, data);
        }

        /// <summary>读取一行控制消息（内部从 type=0x01 帧解出）；非控制帧或失败返回 null。</summary>
        public string ReadLine()
        {
            byte type;
            byte[] data;
            if (!ReadFrame(out type, out data))
            {
                return null;
            }
            if (type != FrameTypeControl)
            {
                return null;
            }
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>关闭隧道并释放 socket。</summary>
        public void Close()
        {
            lock (sync)
            {
                if (tcpClient == null && stream == null && !connected)
                {
                    return;
                }

                try
                {
                    if (stream != null)
                    {
                        stream.Close();
                    }
                }
                catch
                {
                    // 忽略关闭异常。
                }

                try
                {
                    if (tcpClient != null)
                    {
                        tcpClient.Close();
                    }
                }
                catch
                {
                    // 忽略关闭异常。
                }

                stream = null;
                tcpClient = null;
                connected = false;
            }

            PhoneBoxLog.Info("中继隧道已关闭。");
        }

        public void Dispose()
        {
            Close();
            lock (sync)
            {
                disposed = true;
            }
        }

        /// <summary>发送一行纯文本到服务器（仅配对握手阶段使用）。</summary>
        private void WriteRawLine(string line)
        {
            lock (sync)
            {
                EnsureConnected();
                byte[] bytes = Encoding.ASCII.GetBytes(line + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        /// <summary>读取一行纯文本（仅配对握手阶段使用）。</summary>
        private string ReadRawLine()
        {
            NetworkStream s;
            lock (sync)
            {
                s = stream;
            }
            if (s == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            int c;
            while ((c = s.ReadByte()) >= 0)
            {
                if (c == '\n')
                {
                    break;
                }
                if (c != '\r')
                {
                    sb.Append((char)c);
                }
            }
            return sb.ToString();
        }

        private void EnsureConnected()
        {
            if (!connected || stream == null)
            {
                throw new InvalidOperationException("中继隧道尚未连接。");
            }
        }

        private static void WriteInt32Be(Stream s, int value)
        {
            s.WriteByte((byte)((value >> 24) & 0xFF));
            s.WriteByte((byte)((value >> 16) & 0xFF));
            s.WriteByte((byte)((value >> 8) & 0xFF));
            s.WriteByte((byte)(value & 0xFF));
        }

        private static int ReadInt32Be(Stream s)
        {
            int b0 = s.ReadByte();
            int b1 = s.ReadByte();
            int b2 = s.ReadByte();
            int b3 = s.ReadByte();
            if (b3 < 0)
            {
                return -1;
            }
            return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        }

        private static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, offset + read, count - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException("中继隧道在帧中途断开。");
                }
                read += n;
            }
        }
    }
}
