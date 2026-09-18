using System;
using System.IO;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 预览器诊断日志：受 RenderSettings.DiagnosticsLogEnabled 开关控制。
    /// 写入 %LOCALAPPDATA%\3DThumbnailShell\previewer.log，便于排查加载/渲染/缩略图等 bug。
    /// 静态方法任意线程可调；开关关闭时不产生任何磁盘写入。
    /// </summary>
    public static class DiagnosticLog
    {
        private static readonly object _gate = new object();
        private static bool? _enabled; // 缓存开关（首次懒加载，减少反复读盘）

        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "3DThumbnailShell", "previewer.log");

        public static bool IsEnabled
        {
            get
            {
                if (!_enabled.HasValue) _enabled = RenderSettings.Load().DiagnosticsLogEnabled;
                return _enabled.Value;
            }
        }

        /// <summary>刷新缓存（外部改设置后调用，如设置窗口"应用"）。</summary>
        public static void RefreshEnabled()
        {
            try { _enabled = RenderSettings.Load().DiagnosticsLogEnabled; }
            catch { _enabled = false; }
        }

        /// <summary>写入一行日志（带时间戳与线程名）；开关关闭或失败时静默忽略。</summary>
        public static void Log(string msg)
        {
            if (!IsEnabled) return;
            try
            {
                lock (_gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" +
                        System.Threading.Thread.CurrentThread.ManagedThreadId + "] " + msg + "\r\n");
                }
            }
            catch { }
        }
    }
}