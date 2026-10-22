using System;
using System.Threading.Tasks;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 缓存自动清理调度：读 AppSettings 阈值，超阈值则清理到 90%（留余量防频繁触发）。
    /// 由 MainWindow 在启动 / 30s 轮询 / 关闭时触发，_cleaning 标志防重入。
    /// </summary>
    public static class CacheCleaner
    {
        private static readonly object Lock = new object();
        private static bool _cleaning;

        /// <summary>清理阈值（字节）。读设置并 clamp 下限 100MB。</summary>
        public static long ThresholdBytes
        {
            get
            {
                var mb = AppSettingsStore.Current.CacheCleanThresholdMB;
                if (mb < 100)
                {
                    mb = 100;
                }
                return (long)mb * 1024L * 1024L;
            }
        }

        /// <summary>是否正在测量或清理。</summary>
        public static bool IsCleaning
        {
            get
            {
                lock (Lock)
                {
                    return _cleaning;
                }
            }
        }

        /// <summary>
        /// 检查并（如超阈值）后台清理缓存。
        /// <paramref name="onMeasured"/> 在测量完成后（UI 线程）回调实际大小；
        /// <paramref name="onCleaned"/> 在真正执行清理后（UI 线程）回调。
        /// </summary>
        public static void EnsureBelowThreshold(Action<long> onMeasured = null, Action onCleaned = null)
        {
            lock (Lock)
            {
                if (_cleaning)
                {
                    return;
                }
                _cleaning = true;
            }

            Task.Run(() => CacheManager.GetCacheSize())
                .ContinueWith(t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        lock (Lock)
                        {
                            _cleaning = false;
                        }
                        return;
                    }

                    var size = t.Result;
                    onMeasured?.Invoke(size);

                    if (size >= ThresholdBytes)
                    {
                        var target = (long)(ThresholdBytes * 0.9);
                        CacheManager.CleanToTarget(target, () =>
                        {
                            lock (Lock)
                            {
                                _cleaning = false;
                            }
                            onCleaned?.Invoke();
                        });
                    }
                    else
                    {
                        lock (Lock)
                        {
                            _cleaning = false;
                        }
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
