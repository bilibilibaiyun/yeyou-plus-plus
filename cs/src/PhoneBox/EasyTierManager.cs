using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// EasyTier 跨网络组网管理器：内嵌 easytier-core.exe / easytier-cli.exe，
    /// 负责启动组网（需管理员权限）、查询虚拟 IP、停止组网。
    ///
    /// 组网成功后，电脑与手机处于同一虚拟局域网（跨 NAT 打洞 / 公共节点中继），
    /// 手机可用电脑的虚拟 IP 直连 TCP 8760 + UDP 8761，复用现有投屏链路。
    /// </summary>
    public sealed class EasyTierManager : IDisposable
    {
        /// <summary>
        /// EasyTier 公共共享节点（打洞引导 + 兜底中继）。
        /// 同时连接多个节点，提高校园网等复杂网络下的可用性。
        ///
        /// 注意：官方节点 public.easytier.cn / public.easytier.top 已于 2026-02 停止服务
        /// （域名已注销），此处改用社区仍在维护的公共节点（实测可分配虚拟 IP）。
        /// </summary>
        private static readonly string[] PublicNodes = new[]
        {
            "tcp://easytier.weiai.org.cn:11010",
            "tcp://ros.scpsl.com.cn:11010",
            "tcp://boi.de5.net:11010"
        };

        private readonly string easyTierDir;
        private Process coreProcess;

        public EasyTierManager()
        {
            easyTierDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "easytier");
        }

        /// <summary>EasyTier 运行时文件是否就绪。</summary>
        public bool IsAvailable
        {
            get
            {
                return File.Exists(Path.Combine(easyTierDir, "easytier-core.exe"))
                    && File.Exists(Path.Combine(easyTierDir, "easytier-cli.exe"));
            }
        }

        /// <summary>当前虚拟 IP（组网成功后非空）。</summary>
        public string VirtualIp { get; private set; }

        /// <summary>是否正在组网。</summary>
        public bool IsRunning
        {
            get { return coreProcess != null && !coreProcess.HasExited; }
        }

        /// <summary>
        /// 启动组网实例。需要管理员权限（创建 TUN 虚拟网卡），会触发 UAC 提权。
        /// </summary>
        public void Start(string networkName, string networkSecret)
        {
            if (IsRunning)
            {
                return;
            }

            var corePath = Path.Combine(easyTierDir, "easytier-core.exe");
            // 引号包裹避免网络名/密钥含特殊字符导致命令行注入。
            // 多公共节点 + 全协议监听：最大化校园网（对称 NAT / 防火墙 / 认证）下的穿透成功率，
            // 打洞失败时由公共节点中继兜底。
            var args = "--network-name \"" + networkName + "\""
                + " --network-secret \"" + networkSecret + "\""
                + " --dhcp true";
            foreach (var node in PublicNodes)
            {
                // 用 -p（--peers）连接公共节点；-e（--external-node）只能使用一次，
                // 多次会报错 "cannot be used multiple times"。
                args += " -p " + node;
            }
            args += " -l tcp://0.0.0.0:11010"
                + " -l udp://0.0.0.0:11010"
                + " -l ws://0.0.0.0:11011"
                + " -l wss://0.0.0.0:11012";
            // 注意：不要加 quic://0.0.0.0:11010——QUIC 底层也是 UDP，与 udp://11010
            // 端口冲突，会导致 easytier-core 启动失败（os error 10048）。

            var psi = new ProcessStartInfo
            {
                FileName = corePath,
                Arguments = args,
                WorkingDirectory = easyTierDir,
                UseShellExecute = true,      // runas 提权必须用 ShellExecute
                Verb = "runas",              // 触发 UAC
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            coreProcess = Process.Start(psi);
            PhoneBoxLog.Info("EasyTier 组网已启动（网络 " + networkName + "）。");
        }

        /// <summary>轮询查询本机虚拟 IP，组网未就绪时最长等待 timeoutSeconds 秒。</summary>
        public string QueryVirtualIp(int timeoutSeconds = 25)
        {
            var cliPath = Path.Combine(easyTierDir, "easytier-cli.exe");
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var ip = QueryOnce(cliPath);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        VirtualIp = ip;
                        return ip;
                    }
                }
                catch
                {
                    // 组网尚未就绪，继续等待。
                }
                Thread.Sleep(500);
            }
            return null;
        }

        /// <summary>调用 easytier-cli 查询本机节点信息，返回 ipv4_addr（组网未就绪时返回 null）。</summary>
        private static string QueryOnce(string cliPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "-o json node info",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode != 0)
                {
                    return null;
                }
                var obj = JObject.Parse(stdout);
                string ip = (string)obj["ipv4_addr"];
                if (string.IsNullOrEmpty(ip))
                {
                    return null;
                }
                // ipv4_addr 形如 "10.126.126.1/24"，联机码只需纯 IPv4，去掉 /CIDR 后缀。
                int slash = ip.IndexOf('/');
                return slash >= 0 ? ip.Substring(0, slash) : ip;
            }
        }

        /// <summary>停止组网进程。</summary>
        public void Stop()
        {
            if (coreProcess != null && !coreProcess.HasExited)
            {
                try
                {
                    coreProcess.Kill();
                }
                catch
                {
                    // 忽略进程已退出。
                }
            }
            coreProcess = null;
            VirtualIp = null;
            PhoneBoxLog.Info("EasyTier 组网已停止。");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
