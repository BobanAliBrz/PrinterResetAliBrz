using System;
using System.IO;
using System.Linq;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Clears orphaned .SPL and .SHD files from the spool directory.
    /// These leftover files indicate jobs that crashed mid-print and can block new jobs.
    /// </summary>
    public class StaleFileCleaner
    {
        private static readonly string SpoolDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"spool\PRINTERS");

        /// <summary>
        /// Deletes all spool files older than the specified threshold.
        /// Returns the count of files cleaned.
        /// </summary>
        public int CleanStaleFiles(int olderThanSeconds)
        {
            if (!Directory.Exists(SpoolDirectory))
            {
                Logger.Warn($"Spool directory not found: {SpoolDirectory}");
                return 0;
            }

            int cleaned = 0;
            var cutoff = DateTime.Now.AddSeconds(-olderThanSeconds);

            try
            {
                var files = Directory.GetFiles(SpoolDirectory, "*.SPL")
                    .Concat(Directory.GetFiles(SpoolDirectory, "*.SHD"));

                foreach (var filePath in files)
                {
                    try
                    {
                        var fi = new FileInfo(filePath);
                        if (fi.LastWriteTime < cutoff)
                        {
                            fi.Delete();
                            Logger.Info($"Cleaned stale spool file: {Path.GetFileName(filePath)}");
                            cleaned++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not delete spool file {filePath}: {ex.Message}");
                        // File might be locked by spooler — skip it
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to scan spool directory: {ex.Message}");
            }

            if (cleaned > 0)
                Logger.Info($"Cleaned {cleaned} stale spool file(s) from {SpoolDirectory}");

            return cleaned;
        }
    }
}