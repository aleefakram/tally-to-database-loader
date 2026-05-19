using System;
using System.IO;

namespace TallyDbLoader.Core.Logging
{
    public static class FileLogger
    {
        private static readonly object _lock = new object();
        private static readonly string ImportLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "import-log.txt");
        private static readonly string ErrorLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error-log.txt");
        private static bool _initialized = false;

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;

                try
                {
                    if (File.Exists(ImportLogPath))
                        File.Delete(ImportLogPath);
                }
                catch {}

                try
                {
                    if (File.Exists(ErrorLogPath))
                        File.Delete(ErrorLogPath);
                }
                catch {}

                _initialized = true;
            }
        }

        public static void LogMessage(string message)
        {
            Initialize();
            lock (_lock)
            {
                try
                {
                    File.AppendAllText(ImportLogPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                }
                catch {}
            }
        }

        public static void LogError(string context, Exception ex)
        {
            Initialize();
            lock (_lock)
            {
                try
                {
                    string errText = $"Error from {context} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                                     $"Message: {ex.Message}{Environment.NewLine}" +
                                     $"Stack Trace:{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}" +
                                     new string('-', 80) + Environment.NewLine + Environment.NewLine;
                    File.AppendAllText(ErrorLogPath, errText);
                }
                catch {}
            }
        }
    }
}
