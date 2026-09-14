using System;
using System.IO;
using System.Threading;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 应用级文件日志(诊断用)。写入用户数据目录下的 DSH Launcher\Settings\app.log
    /// (Windows:%LOCALAPPDATA%;macOS:~/Library/Application Support)。
    /// 所有写入失败均静默忽略,不影响应用功能。
    /// </summary>
    public static class AppLogService
    {
        /// <summary>单文件大小上限,超过后重新开始(简单滚动)。</summary>
        private const long MaxFileLengthBytes = 1 * 1024 * 1024;

        private static readonly Lock WriteLock = new();

        /// <summary>
        /// 日志文件路径。必须是延迟求值:应用启动极早期解析用户目录可能瞬时为空,
        /// 用 static readonly 字段会把空值永久固化(与 SettingsService 同样的防御)。
        /// </summary>
        public static string LogFilePath => Path.Combine(
            PlatformProcess.LocalAppDataDirectory, "DSH Launcher", "Settings", "app.log");


        /// <summary>在日志中标记一次应用启动(分隔线 + 时间)。</summary>
        public static void MarkSessionStart()
        {
            Write($"===== 应用启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }

        /// <summary>追加一条日志(带毫秒级时间戳,线程安全,失败静默)。</summary>
        public static void Write(string message)
        {
            try
            {
                var directory = Path.GetDirectoryName(LogFilePath)!;
                Directory.CreateDirectory(directory);

                lock (WriteLock)
                {
                    // 超过上限时清空重写,避免无限增长
                    if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > MaxFileLengthBytes)
                    {
                        File.Delete(LogFilePath);
                    }

                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}\r\n";
                    File.AppendAllText(LogFilePath, line);
                }
            }
            catch (Exception)
            {
                // 日志写入失败不影响应用功能
            }
        }
    }
}
