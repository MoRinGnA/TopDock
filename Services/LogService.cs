using System;
using System.IO;

namespace TopDock.Services
{
    /// <summary>
    /// 파일 기반 로깅. %APPDATA%\TopDock\Logs\log-YYYYMMDD.log (7일 보관)
    /// 앱 어디서든 Log.Info(...) / Log.Warn(...) / Log.Error(...) 형태로 사용.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new();
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TopDock", "Logs");
        private const int RetentionDays = 7;

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception? ex = null)
            => Write("ERROR", ex == null ? message : $"{message} :: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDir);
                    string file = Path.Combine(LogDir, $"log-{DateTime.Now:yyyyMMdd}.log");
                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(file, line);
                    CleanupOldLogs();
                }
            }
            catch
            {
                // 로깅 자체의 실패가 앱을 죽이지 않도록 조용히 무시
            }
        }

        private static void CleanupOldLogs()
        {
            try
            {
                foreach (string file in Directory.GetFiles(LogDir, "log-*.log"))
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-RetentionDays))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch { }
        }
    }
}
