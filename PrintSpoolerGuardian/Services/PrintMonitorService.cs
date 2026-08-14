using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Management;
using System.Threading;

namespace PrintSpoolerGuardian
{
    /// <summary>Background monitor compatible with Windows 7's inbox .NET 3.5.1.</summary>
    public class PrintMonitorService
    {
        private readonly RecoveryEngine _recoveryEngine = new RecoveryEngine();
        private readonly PrintJobDetector _detector = new PrintJobDetector();
        private readonly StaleFileCleaner _fileCleaner = new StaleFileCleaner();
        private readonly AutoUpdater _autoUpdater = new AutoUpdater();
        private readonly int _pollIntervalSeconds;
        private readonly int _staleFileThresholdSeconds;
        private volatile bool _stopRequested;
        private bool _paused;
        private DateTime _pauseUntil = DateTime.MinValue;
        private readonly Dictionary<string, DateTime> _alertedJobs = new Dictionary<string, DateTime>();
        private const int AlertExpirySeconds = 600;

        public DateTime LastCheckTime { get; private set; }
        public int RecoveriesThisHour { get { return _recoveryEngine.RecoveriesThisHour; } }
        public string[] WatchedPrinterNames { get; private set; }

        public PrintMonitorService()
        {
            _pollIntervalSeconds = GetSetting("PollIntervalSeconds", 30);
            _staleFileThresholdSeconds = GetSetting("StaleFileThresholdSeconds", 300);
            var watched = (ConfigurationManager.AppSettings["WatchedPrinters"] ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            WatchedPrinterNames = watched;
        }

        public string GetStatus()
        {
            if (_paused) return "Paused until " + _pauseUntil.ToString("HH:mm:ss");
            if (LastCheckTime == DateTime.MinValue) return "Not yet started";
            return "Last check: " + LastCheckTime.ToString("HH:mm:ss") + ", Recoveries: " + RecoveriesThisHour + ", State: " + (_recoveryEngine.IsInCooldown ? "COOLDOWN" : "ACTIVE") + ", Rate limit: " + _recoveryEngine.IsRateLimited;
        }

        public void Run()
        {
            _stopRequested = false;
            Logger.Info("Print Spooler Guardian started. Poll interval: " + _pollIntervalSeconds + "s");
            Logger.Info("Watched printers: " + (WatchedPrinterNames.Length > 0 ? string.Join(", ", WatchedPrinterNames) : "(all)"));
            _autoUpdater.Start();
            var watcherThread = new Thread(WatchPrintEvents) { IsBackground = true, Name = "Print WMI event watcher" };
            watcherThread.Start();

            while (!_stopRequested)
            {
                if (_paused && DateTime.Now < _pauseUntil) { SleepWithStop(5000); continue; }
                if (_paused) { _paused = false; Logger.Info("Monitoring unpaused."); }
                try { CheckOnce(); } catch (Exception ex) { Logger.Error("Error during monitoring check: " + ex.Message); }
                SleepWithStop(_pollIntervalSeconds * 1000);
            }
            _autoUpdater.Stop();
            Logger.Info("Print Spooler Guardian stopped.");
        }

        public void RequestStop() { _stopRequested = true; }
        public void Pause(int minutes) { _paused = true; _pauseUntil = DateTime.Now.AddMinutes(minutes); Logger.Info("Monitoring paused until " + _pauseUntil.ToString("HH:mm:ss")); }
        public void Unpause() { _paused = false; Logger.Info("Monitoring unpaused manually."); }

        public void ForceRecovery()
        {
            var printers = _detector.GetProblemPrinters();
            if (printers.Count == 0)
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, PortName FROM Win32_Printer"))
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"] == null ? "" : obj["Name"].ToString();
                    var port = obj["PortName"] == null ? "" : obj["PortName"].ToString();
                    printers.Add(new PrinterConfig { Name = name, PortName = port, ConnectionType = PrintJobDetector.ClassifyConnection(port) });
                }
            }
            foreach (var printer in printers)
            {
                Logger.Info("Force recovery triggered for: " + printer.Name + " [" + printer.ConnectionType + "]");
                foreach (var action in _recoveryEngine.ExecuteRecovery(printer)) Logger.Info("  -> " + action);
            }
        }

        private void CheckOnce()
        {
            LastCheckTime = DateTime.Now;
            var jobs = _detector.GetProblematicJobs(WatchedPrinterNames.Length > 0 ? WatchedPrinterNames : null);
            var problemPrinters = _detector.GetProblemPrinters();
            var cleaned = _fileCleaner.CleanStaleFiles(_staleFileThresholdSeconds);
            if (cleaned > 0) Logger.Info("Cleaned " + cleaned + " stale spool file(s)");
            var types = problemPrinters.ToDictionary(p => p.Name, p => p.ConnectionType, StringComparer.OrdinalIgnoreCase);
            var targets = new List<PrinterConfig>();
            foreach (var job in jobs.GroupBy(j => j.PrinterName).Select(g => g.First()))
            {
                var key = job.PrinterName + ":" + job.JobId;
                DateTime alerted;
                if (_alertedJobs.TryGetValue(key, out alerted) && (DateTime.Now - alerted).TotalSeconds < AlertExpirySeconds) continue;
                if (job.IsProblematic)
                {
                    PrinterConnectionType type;
                    targets.Add(new PrinterConfig { Name = job.PrinterName, ConnectionType = types.TryGetValue(job.PrinterName, out type) ? type : PrinterConnectionType.Shared });
                    _alertedJobs[key] = DateTime.Now;
                }
            }
            targets.AddRange(problemPrinters);
            var unique = targets.GroupBy(p => p.Name).Select(g => g.First()).ToList();
            if (unique.Count > 0)
            {
                Logger.Warn("Detected " + unique.Count + " printer(s) with problems: " + string.Join(", ", unique.Select(p => p.Name).ToArray()));
                foreach (var printer in unique) foreach (var action in _recoveryEngine.ExecuteRecovery(printer)) Logger.Info("  [" + printer.Name + "] " + action);
            }
            foreach (var key in _alertedJobs.Where(kv => (DateTime.Now - kv.Value).TotalSeconds >= AlertExpirySeconds).Select(kv => kv.Key).ToList()) _alertedJobs.Remove(key);
        }

        private void WatchPrintEvents()
        {
            try
            {
                var query = new WqlEventQuery("SELECT * FROM __InstanceModificationEvent WITHIN 10 WHERE TargetInstance ISA 'Win32_PrintJob'");
                using (var watcher = new ManagementEventWatcher(query))
                {
                    watcher.EventArrived += delegate(object sender, EventArrivedEventArgs e) { try { var job = e.NewEvent["TargetInstance"] as ManagementBaseObject; if (job == null) return; uint status = job["JobStatus"] == null ? 0 : Convert.ToUInt32(job["JobStatus"]); if ((status & 0x132) != 0) Logger.Warn("Event detected: print job status " + status); } catch { } };
                    watcher.Start(); Logger.Info("WMI event watcher started for print jobs.");
                    while (!_stopRequested) Thread.Sleep(1000);
                    watcher.Stop();
                }
            }
            catch (Exception ex) { Logger.Warn("WMI event watcher failed (will rely on polling): " + ex.Message); }
        }

        private void SleepWithStop(int milliseconds) { for (int elapsed = 0; elapsed < milliseconds && !_stopRequested; elapsed += 500) Thread.Sleep(Math.Min(500, milliseconds - elapsed)); }
        private static int GetSetting(string key, int fallback) { int value; return int.TryParse(ConfigurationManager.AppSettings[key], out value) ? value : fallback; }
    }
}
