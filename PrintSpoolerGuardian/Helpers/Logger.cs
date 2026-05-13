using System;
using System.IO;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Lightweight file logger — no external dependencies.
    /// </summary>
    public static class Logger
    {
        private static readonly string LogDirectory;
        private static readonly string LogFilePath;
        private static readonly object LockObj = new object();

        static Logger()
        {
            LogDirectory = System.Configuration.ConfigurationManager.AppSettings["LogDirectory"]
                ?? @"C:\ProgramData\PrintSpoolerGuardian";

            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);

            LogFilePath = Path.Combine(LogDirectory, "PrintSpoolerGuardian.log");
        }

        public static void Info(string message) => Log("INFO", message);
        public static void Warn(string message) => Log("WARN", message);
        public static void Error(string message, Exception ex = null)
            => Log("ERROR", message + (ex != null ? $" | {ex.Message}" : ""));
        public static void Debug(string message) => Log("DEBUG", message);

        private static void Log(string level, string message)
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            lock (LockObj)
            {
                try
                {
                    File.AppendAllText(LogFilePath, entry + Environment.NewLine);

                    // Rotate log if it exceeds 5 MB
                    var fi = new FileInfo(LogFilePath);
                    if (fi.Length > 5 * 1024 * 1024)
                    {
                        var backupPath = Path.Combine(LogDirectory,
                            $"PrintSpoolerGuardian_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                        File.Move(LogFilePath, backupPath);
                    }
                }
                catch { /* Fail silently — we're a guardian, not a crasher */ }
            }
        }
    }
}