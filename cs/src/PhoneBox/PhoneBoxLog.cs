using System;
using System.IO;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 手机盒子日志工具：写 AppPaths.DataDir\logs\phonebox.log。
    /// </summary>
    public static class PhoneBoxLog
    {
        private static readonly object Sync = new object();

        /// <summary>日志文件完整路径。</summary>
        public static string LogFile
        {
            get { return Path.Combine(AppPaths.DataDir, "logs", "phonebox.log"); }
        }

        /// <summary>记录信息级别日志。</summary>
        public static void Info(string message)
        {
            Write("INFO", message);
        }

        /// <summary>记录警告级别日志。</summary>
        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        /// <summary>记录错误级别日志。</summary>
        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            lock (Sync)
            {
                try
                {
                    var dir = Path.GetDirectoryName(LogFile);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.AppendAllText(LogFile,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine);
                }
                catch
                {
                    // 日志写入失败一律吞掉，绝不因日志影响主流程。
                }
            }
        }
    }
}
