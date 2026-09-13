using System;
using System.IO;
using System.Threading;

namespace DSH_Launcher.Services
{
    /// <summary>
    /// 应用级文件日志(诊断用)。写入 %LOCALAPPDATA%\DSH Launcher\Settings\app.log。
    /// 所有写入失败均静默忽略,不影响应用功能。
    /// </summary>
    public static class AppLogService
    {
        /// <summary>单文件大小上限,超过后重新开始(简单滚动)。</summary>
        private const long MaxFileLengthBytes = 1 * 1024 * 1024;

        private static readonly Lock WriteLock = new();

        private static readonly string LogFilePath = Path.Combine(LocalAppDataDirectory, "DSH Launcher", "Settings", "app.log");

        /// <summary>
        /// 解析 LocalAppData 目录。与 SettingsService 同样的防御:
        /// GetFolderPath 异常时回退 %LOCALAPPDATA% 环境变量。
        /// </summary>
        private static string LocalAppDataDirectory
        {
            get
            {
                var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(path))
                {
                    path = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
                }
                return path;
            }
        }


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
