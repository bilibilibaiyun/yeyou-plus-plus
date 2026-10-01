using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace YeyouPlusPlus
{
    /// <summary>GitHub Release 摘要。</summary>
    public class ReleaseInfo
    {
        public string Tag { get; set; }
        public string Name { get; set; }
        public bool IsStable { get; set; }
        public DateTime PublishedAt { get; set; }
        public string AssetUrl { get; set; }
        public long AssetSize { get; set; }

        public string TypeText { get { return IsStable ? "稳定版" : "测试版"; } }
    }

    /// <summary>
    /// 检查更新：读取 GitHub Releases，区分稳定版 / 测试版，支持下载安装包。
    /// </summary>
    public static class UpdateChecker
    {
        public const string RepoOwner = "bilibilibaiyun";
        public const string RepoName = "yeyou-plus-plus";
        public const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;

        private static readonly string ApiUrl =
            "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases?per_page=30";

        /// <summary>复用连接的单例 HttpClient；默认超时 5 分钟（下载安装包需要更长时间）。</summary>
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        static UpdateChecker()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("YeyouPlusPlus-Updater");
        }

        /// <summary>当前程序版本（取程序集版本，格式 X.Y.Z）。</summary>
        public static string CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "0.0.0" : v.ToString(3);
            }
        }

        /// <summary>拉取全部 Release（按 GitHub 返回顺序，最新在前）。网络失败抛异常。</summary>
        public static List<ReleaseInfo> GetReleases()
        {
            // 检查更新用 15s 超时（下载用 HttpClient 默认的 5 分钟）。
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl))
            {
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using (var resp = Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                    .GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var raw = JsonConvert.DeserializeObject<List<GitHubRelease>>(json)
                        ?? new List<GitHubRelease>();

                    var result = new List<ReleaseInfo>();
                    foreach (var r in raw)
                    {
                        if (r.draft)
                        {
                            continue;
                        }

                        var asset = (r.assets ?? new List<GitHubAsset>()).FirstOrDefault(a =>
                            a.name != null && a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                        if (asset == null)
                        {
                            continue;
                        }

                        result.Add(new ReleaseInfo
                        {
                            Tag = (r.tag_name ?? string.Empty).TrimStart('v'),
                            Name = string.IsNullOrWhiteSpace(r.name) ? r.tag_name : r.name,
                            IsStable = !r.prerelease,
                            PublishedAt = ParseDate(r.published_at),
                            AssetUrl = asset.browser_download_url,
                            AssetSize = asset.size
                        });
                    }
                    return result;
                }
            }
        }

        /// <summary>取列表中最新的稳定版（无则 null）。</summary>
        public static ReleaseInfo LatestStable(List<ReleaseInfo> releases)
        {
            return releases == null ? null : releases.FirstOrDefault(x => x.IsStable);
        }

        /// <summary>candidate 版本号是否大于 current。</summary>
        public static bool IsNewer(string candidate, string current)
        {
            var a = ParseVersion(candidate);
            var b = ParseVersion(current);
            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i] > b[i];
                }
            }
            return false;
        }

        /// <summary>
        /// 下载安装包（同步阻塞，请放到后台线程调用）。progress 传 0~100。
        /// </summary>
        public static void DownloadFile(string url, string destPath, Action<int> progress)
        {
            // ResponseHeadersRead：尽早拿到响应流（不等正文），实现边下边回调进度；
            // 避免 HttpWebRequest 的 GetResponse 在 DNS/连接/TLS/重定向阶段无反馈地卡住。
            using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[65536];
                    long read = 0;
                    int n;
                    int lastPct = -1;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, n);
                        read += n;
                        // ContentLength 为 null 时不回调进度（保持 0），下载完成后统一报 100。
                        if (total.HasValue && total.Value > 0 && progress != null)
                        {
                            var pct = (int)(read * 100 / total.Value);
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                progress(pct);
                            }
                        }
                    }
                    progress?.Invoke(100);
                }
            }
        }

        /// <summary>启动安装程序（静默覆盖安装，保留配置与数据），返回是否成功启动。</summary>
        public static bool LaunchInstaller(string setupPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime ParseDate(string s)
        {
            DateTime d;
            return DateTime.TryParse(s, out d) ? d : DateTime.MinValue;
        }

        private static int[] ParseVersion(string v)
        {
            var result = new[] { 0, 0, 0 };
            if (string.IsNullOrWhiteSpace(v))
            {
                return result;
            }
            var parts = v.TrimStart('v', 'V').Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int n;
                if (int.TryParse(parts[i], out n))
                {
                    result[i] = n;
                }
            }
            return result;
        }

        // ---- GitHub API DTO ----

        private class GitHubRelease
        {
            public string tag_name { get; set; }
            public string name { get; set; }
            public bool prerelease { get; set; }
            public bool draft { get; set; }
            public string published_at { get; set; }
            public List<GitHubAsset> assets { get; set; }
        }

        private class GitHubAsset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
            public long size { get; set; }
        }
    }
}
