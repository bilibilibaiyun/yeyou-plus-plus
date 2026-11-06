using Newtonsoft.Json;
using System;
using System.IO;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 手机盒子运行配置。保持独立配置，不接入现有 AppSettings。
    /// </summary>
    public class PhoneBoxConfig
    {
        /// <summary>控制通道 TCP 监听端口。</summary>
        public int TcpPort { get; set; } = 8760;

        /// <summary>视频 / 数据 UDP 端口。</summary>
        public int UdpPort { get; set; } = 8761;

        /// <summary>捕获 / 推流分辨率宽。</summary>
        public int Width { get; set; } = 1280;

        /// <summary>捕获 / 推流分辨率高。</summary>
        public int Height { get; set; } = 720;

        /// <summary>目标帧率。</summary>
        public int FrameRate { get; set; } = 30;

        /// <summary>目标码率（kbps）。</summary>
        public int BitrateKbps { get; set; } = 2500;

        /// <summary>默认按键布局（JSON 字符串，供手机端渲染按钮）。</summary>
        public string KeyLayout { get; set; } = DefaultKeyLayout;

        /// <summary>默认按键布局：方向键 + 两个常用动作键。</summary>
        public const string DefaultKeyLayout =
            "{\"keys\":[" +
            "{\"name\":\"up\",\"keyCode\":38}," +
            "{\"name\":\"down\",\"keyCode\":40}," +
            "{\"name\":\"left\",\"keyCode\":37}," +
            "{\"name\":\"right\",\"keyCode\":39}," +
            "{\"name\":\"action1\",\"keyCode\":88}," +
            "{\"name\":\"action2\",\"keyCode\":90}" +
            "]}";
    }

    /// <summary>
    /// 手机盒子配置读写（简单 get/set + JSON 序列化）。
    /// </summary>
    public static class PhoneBoxConfigStore
    {
        private static readonly object Sync = new object();
        private static PhoneBoxConfig current;

        /// <summary>配置文件路径：数据目录 phonebox.json。</summary>
        private static string FilePath
        {
            get { return Path.Combine(AppPaths.DataDir, "phonebox.json"); }
        }

        /// <summary>当前配置（首次访问时加载，损坏或缺失则回退默认值）。</summary>
        public static PhoneBoxConfig Current
        {
            get
            {
                lock (Sync)
                {
                    if (current == null)
                    {
                        current = Load();
                    }
                    return current;
                }
            }
        }

        /// <summary>保存当前配置，失败静默忽略。</summary>
        public static void Save()
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(FilePath,
                        JsonConvert.SerializeObject(Current, Formatting.Indented));
                }
                catch
                {
                    // 配置写入失败不阻塞主流程。
                }
            }
        }

        private static PhoneBoxConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonConvert.DeserializeObject<PhoneBoxConfig>(File.ReadAllText(FilePath))
                        ?? new PhoneBoxConfig();
                }
            }
            catch
            {
                // 配置损坏时回退默认值。
            }
            return new PhoneBoxConfig();
        }
    }
}
