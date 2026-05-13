using System;
using System.Collections.Generic;
using System.Configuration;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// The main monitoring loop. Runs as a background task.
    /// Subscribes to WMI print events and also polls periodically.
    /// </summary>
    public class PrintMonitorService
    {
        private readonly RecoveryEngine _recoveryEngine;
        private readonly PrintJobDetector _detector;
        private readonly StaleFileCleaner _fileCleaner;
        private readonly AutoUpdater _autoUpdater;

        private readonly int _pollIntervalSeconds;
        private readonly int _staleJobThresholdSeconds;
        private readonly int _staleFileThresholdSeconds;

        private CancellationToken _token;
        private bool _paused;
        private DateTime _pauseUntil = DateTime.MinValue;

        // Track jobs we've already alerted on to avoid duplicate recovery triggers
        private readonly Dictionary<string, DateTime> _alertedJobs = new Dictionary<string, DateTime>();
        private const int AlertExpirySeconds = 600; // 10 minutes

        public DateTime LastCheckTime { get; private set; }
        public int RecoveriesThisHour => _recoveryEngine.RecoveriesThisHour;

        public PrintMonitorService()
        {
            _recoveryEngine = new RecoveryEngine();
            _detector = new PrintJobDetector();
            _fileCleaner = new StaleFileCleaner();
            _autoUpdater = new AutoUpdater();

            _pollIntervalSeconds = int.TryParse(
                ConfigurationManager.AppSettings["PollIntervalSeconds"] ?? "30", out var pi) ? pi : 30;
            _staleJobThresholdSeconds = int.TryParse(
                ConfigurationManager.AppSettings["StaleJobThresholdSeconds"] ?? "300", out var sj) ? sj : 300;
            _staleFileThresholdSeconds = int.TryParse(
                ConfigurationManager.AppSettings["StaleFileThresholdSeconds"] ?? "300", out var sf) ? sf : 300;

            // Get watched printers from config (semicolon-separated)
            var watchedPrinters = (ConfigurationManager.AppSettings["WatchedPrinters"] ?? "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            // Only subscribe if we have specific printers configured
            if (watchedPrinters.Length > 0)
                WatchedPrinterNames = watchedPrinters;
        }

        public string[] WatchedPrinterNames { get; private set; } = Array.Empty<string>();

        public string GetStatus()
        {
            if (_paused)
                return $"Paused until {_pauseUntil:HH:mm:ss}";

            if (LastCheckTime == default)
                return "Not yet started";

            var updateStatus = _autoUpdater.IsEnabled
                ? $", Updates: {_autoUpdater.LastCheckResult}"
                : "";

            return $"Last check: {LastCheckTime:HH:mm:ss}, Recoveries: {RecoveriesThisHour}, " +
                   $"State: {_recoveryEngine.IsInCooldown ? "COOLDOWN" : "ACTIVE"}, " +
                   $"Rate limit: {_recoveryEngine.IsRateLimited}{updateStatus}";
        }

        public async Task RunAsync(CancellationToken token)
        {
            _token = token;
            Logger.Info($"Print Spooler Guardian started. Poll interval: {_pollIntervalSeconds}s");
            Logger.Info($"Watched printers: {(WatchedPrinterNames.Length > 0 ? string.Join(", ", WatchedPrinterNames) : "(all)")}");

            // Start auto-updater
            _autoUpdater.Start();

            // Start WMI event watcher
            var eventTask = Task.Run(() => WatchPrintEvents(token));

            // Main polling loop
            while (!token.IsCancellationRequested)
            {
                if (_paused && DateTime.Now < _pauseUntil)
                {
                    await Task.Delay(5000, token);
                    continue;
                }

                if (_paused && DateTime.Now >= _pauseUntil)
                {
                    _paused = false;
                    Logger.Info("Monitoring unpaused.");
                }

                try
                {
                    await CheckOnceAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error during monitoring check: {ex.Message}");
                }

                // Sleep in small increments so we can respond to cancellation
                for (int i = 0; i < _pollIntervalSeconds * 2; i++)
                {
                    if (token.IsCancellationRequested)
                        break;
                    await Task.Delay(500);
                }
            }

            Logger.Info("Print Spooler Guardian stopped.");
        }

        public void Pause(int minutes = 30)
        {
            _paused = true;
            _pauseUntil = DateTime.Now.AddMinutes(minutes);
            Logger.Info($"Monitoring paused until {_pauseUntil:HH:mm:ss}");
        }

        public void Unpause()
        {
            _paused = false;
            Logger.Info("Monitoring unpaused manually.");
        }

        public async Task ForceRecoveryAsync()
        {
            // Find ALL problem printers (USB + network)
            var problemPrinters = _detector.GetProblemPrinters();

            // Also check all printers even if not reporting errors (user requested force)
            if (problemPrinters.Count == 0)
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PortName FROM Win32_Printer"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name = obj["Name"]?.ToString() ?? "";
                        var port = obj["PortName"]?.ToString() ?? "";

                        problemPrinters.Add(new PrinterConfig
                        {
                            Name = name,
                            PortName = port,
                            ConnectionType = PrintJobDetector.ClassifyConnection(port)
                        });
                    }
                }
            }

            foreach (var printer in problemPrinters)
            {
                Logger.Info($"Force recovery triggered for: {printer.Name} [{printer.ConnectionType}]");
                var actions = await _recoveryEngine.ExecuteRecoveryAsync(printer);
                foreach (var action in actions)
                    Logger.Info($"  -> {action}");
            }
        }

        private async Task CheckOnceAsync()
        {
            LastCheckTime = DateTime.Now;

            // 1. Check for problematic print jobs
            var problemJobs = _detector.GetProblematicJobs(
                WatchedPrinterNames.Length > 0 ? WatchedPrinterNames : null);

            // 2. Check for ALL printers in error state (USB + network)
            var problemPrinters = _detector.GetProblemPrinters();

            // 3. Clean stale spool files periodically
            var cleaned = _fileCleaner.CleanStaleFiles(_staleFileThresholdSeconds);
            if (cleaned > 0)
                Logger.Info($"Cleaned {cleaned} stale spool file(s)");

            // Build set of printers to recover
            var printersToRecover = new List<PrinterConfig>();

            // Build a lookup of printer name -> connection type from error-state printers
            var printerTypeLookup = problemPrinters
                .ToDictionary(p => p.Name, p => p.ConnectionType, StringComparer.OrdinalIgnoreCase);

            // From problematic jobs, extract unique printer names
            if (problemJobs.Count > 0)
            {
                var printerGroups = problemJobs
                    .GroupBy(j => j.PrinterName)
                    .Select(g => g.First());

                foreach (var job in printerGroups)
                {
                    // Check if this job has been alerting recently
                    var key = $"{job.PrinterName}:{job.JobId}";
                    if (_alertedJobs.TryGetValue(key, out var alertedTime))
                    {
                        if ((DateTime.Now - alertedTime).TotalSeconds < AlertExpirySeconds)
                            continue; // Already alerted
                    }

// Check if the job has been stuck long enough
                    if (job.IsProblematic)
                    {
                        // Use connection type from problemPrinters if available;
                        // otherwise treat as shared (safest default for non-USB)
                        var connType = printerTypeLookup.TryGetValue(
                            job.PrinterName, out var knownType)
                            ? knownType
                            : PrinterConnectionType.Shared;

                         printersToRecover.Add(new PrinterConfig
                         {
                             Name = job.PrinterName,
                             ConnectionType = connType
                         });
                        _alertedJobs[key] = DateTime.Now;
                    }
                }
            }

            // From printer status errors
            foreach (var printer in problemPrinters)
            {
                printersToRecover.Add(printer);
            }

            // Deduplicate by printer name
            var uniquePrinters = printersToRecover
                .GroupBy(p => p.Name)
                .Select(g => g.First())
                .ToList();

            if (uniquePrinters.Count > 0)
            {
                Logger.Warn($"Detected {uniquePrinters.Count} printer(s) with problems: " +
                    string.Join(", ", uniquePrinters.Select(p => p.Name)));

                foreach (var printer in uniquePrinters)
                {
                    var actions = await _recoveryEngine.ExecuteRecoveryAsync(printer);
                    foreach (var action in actions)
                        Logger.Info($"  [{printer.Name}] {action}");
                }
            }

            // Clean up old alert entries
            var expiredKeys = _alertedJobs
                .Where(kv => (DateTime.Now - kv.Value).TotalSeconds >= AlertExpirySeconds)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in expiredKeys)
                _alertedJobs.Remove(key);
        }

        public async Task StopAsync()
        {
            _autoUpdater?.Stop();
            _token?.Cancel();
            await Task.Delay(500);
        }

        private void WatchPrintEvents(CancellationToken token)
        {
            try
            {
                // Watch for print job state changes
                var query = new WqlEventQuery(
                    "SELECT * FROM __InstanceModificationEvent " +
                    "WITHIN 10 WHERE TargetInstance ISA 'Win32_PrintJob'");

                using (var watcher = new ManagementEventWatcher(query))
                {
                    watcher.EventArrived += (sender, e) =>
                    {
                        try
                        {
                            var job = e.NewEvent["TargetInstance"] as ManagementBaseObject;
                            if (job == null) return;

                            var status = job["Status"]?.ToString() ?? "";
                            var jobStatus = Convert.ToUInt32(job["JobStatus"] ?? 0);
                            var printerName = job["Name"]?.ToString() ?? "";

                            // Check if this is a problem state
                            if ((jobStatus & 0x00000002) != 0 || // ERROR
                                (jobStatus & 0x00000010) != 0 || // PAPEROUT
                                (jobStatus & 0x00000100) != 0 || // BLOCKED
                                (jobStatus & 0x00000020) != 0)   // NOT_PRINTED
                            {
                                Logger.Warn($"Event detected: {printerName} - job status {jobStatus} ({status})");
                            }
                        }
                        catch { /* Ignore event parsing errors */ }
                    };

                    watcher.Start();
                    Logger.Info("WMI event watcher started for print jobs.");

                    // Keep alive until token is cancelled
                    while (!token.IsCancellationRequested)
                        Thread.Sleep(1000);

                    watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"WMI event watcher failed (will rely on polling): {ex.Message}");
            }
        }
    }
}