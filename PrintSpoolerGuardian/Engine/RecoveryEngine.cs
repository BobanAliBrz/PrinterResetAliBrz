using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Orchestrates the escalating recovery steps for USB and Shared printers.
    /// Routes to the appropriate handler based on connection type.
    /// Tracks cooldowns and per-hour limits to prevent runaway loops.
    /// </summary>
    public class RecoveryEngine
    {
        private readonly SpoolerController _spooler;
        private readonly UsbPrinterResetter _usbResetter;
        private readonly StaleFileCleaner _fileCleaner;
        private readonly PrintJobDetector _detector;

        private readonly List<DateTime> _recoveryHistory = new List<DateTime>();
        private DateTime _lastRecoveryTime = DateTime.MinValue;

        private readonly int _cooldownMinutes;
        private readonly int _maxRecoveriesPerHour;
        private readonly int _spoolerTimeoutSeconds;
        private readonly int _usbResetWaitSeconds;
        private readonly int _stepWaitSeconds;

        public RecoveryEngine()
        {
            _spooler = new SpoolerController();
            _usbResetter = new UsbPrinterResetter();
            _fileCleaner = new StaleFileCleaner();
            _detector = new PrintJobDetector();

            _cooldownMinutes = int.TryParse(
                ConfigurationManager.AppSettings["CooldownMinutes"] ?? "10", out var cm) ? cm : 10;
            _maxRecoveriesPerHour = int.TryParse(
                ConfigurationManager.AppSettings["MaxRecoveriesPerHour"] ?? "3", out var mrh) ? mrh : 3;
            _spoolerTimeoutSeconds = int.TryParse(
                ConfigurationManager.AppSettings["SpoolerTimeoutSeconds"] ?? "60", out var sts) ? sts : 60;
            _usbResetWaitSeconds = int.TryParse(
                ConfigurationManager.AppSettings["UsbResetWaitSeconds"] ?? "15", out var urws) ? urws : 15;
            _stepWaitSeconds = int.TryParse(
                ConfigurationManager.AppSettings["StepWaitSeconds"] ?? "5", out var sws) ? sws : 5;
        }

        public bool IsInCooldown =>
            (DateTime.Now - _lastRecoveryTime).TotalMinutes < _cooldownMinutes;

        public int RecoveriesThisHour =>
            _recoveryHistory.Count(t => (DateTime.Now - t).TotalHours < 1);

        public bool IsRateLimited => RecoveriesThisHour >= _maxRecoveriesPerHour;

        /// <summary>
        /// Executes recovery — routes to USB or Shared handler.
        /// </summary>
        public async Task<List<string>> ExecuteRecoveryAsync(PrinterConfig printer)
        {
            var actions = new List<string>();

            if (IsInCooldown)
            {
                actions.Add($"SKIP: In cooldown ({_cooldownMinutes} min since last recovery)");
                return actions;
            }

            if (IsRateLimited)
            {
                actions.Add($"SKIP: Rate limited ({RecoveriesThisHour}/{_maxRecoveriesPerHour} this hour)");
                return actions;
            }

            Logger.Info($"=== Starting recovery for: {printer.Name} [{printer.ConnectionType}] ===");

            return printer.IsUsb
                ? await ExecuteUsbRecoveryAsync(printer, actions)
                : await ExecuteSharedRecoveryAsync(printer, actions);
        }

        // ==================== USB RECOVERY ====================

        private async Task<List<string>> ExecuteUsbRecoveryAsync(PrinterConfig printer, List<string> actions)
        {
            // Step 1: Cancel stuck jobs
            Logger.Info("Step 1/4: Cancelling stuck print jobs...");
            _spooler.CancelAllJobs(printer.Name);
            actions.Add("Cancelled stuck print jobs");
            await Task.Delay(_stepWaitSeconds * 1000);

            // Step 2: Clean stale spool files
            Logger.Info("Step 2/4: Cleaning stale spool files...");
            var cleaned = _fileCleaner.CleanStaleFiles(_StaleFileThreshold());
            actions.Add($"Cleaned {cleaned} stale spool files");
            await Task.Delay(_stepWaitSeconds * 1000);

            // Step 3: Restart spooler
            Logger.Info("Step 3/4: Restarting Print Spooler...");
            var spoolerOk = await _spooler.RestartAsync(_spoolerTimeoutSeconds);
            if (spoolerOk)
            {
                actions.Add("Restarted Print Spooler (OK)");
                await Task.Delay(_stepWaitSeconds * 1000);

                if (!IsPrinterStillBroken(printer))
                {
                    Logger.Info("Recovery successful after spooler restart.");
                    actions.Add("RESULT: Problem resolved after spooler restart");
                    RecordRecovery();
                    return actions;
                }
            }
            else
            {
                actions.Add("Restarted Print Spooler (FAILED)");
            }

            // Step 4: USB device reset
            Logger.Info("Step 4/4: Resetting USB device...");
            var deviceId = _detector.GetUsbDeviceInstanceId(printer.PortName);

            if (!string.IsNullOrEmpty(deviceId))
            {
                var usbOk = await _usbResetter.ResetAsync(deviceId, _usbResetWaitSeconds);
                if (usbOk)
                {
                    actions.Add($"Reset USB device: {deviceId}");
                    Logger.Info("Restarting spooler after USB reset...");
                    await Task.Delay(_stepWaitSeconds * 1000);
                    var spoolerOk2 = await _spooler.RestartAsync(_spoolerTimeoutSeconds);
                    if (spoolerOk2)
                    {
                        actions.Add("Restarted Print Spooler post-USB-reset (OK)");
                        actions.Add("RESULT: Full recovery (USB reset + spooler restart)");
                    }
                    else
                    {
                        actions.Add("Restarted Print Spooler post-USB-reset (FAILED)");
                    }
                }
                else
                {
                    actions.Add($"USB device reset FAILED: {deviceId}");
                }
            }
            else
            {
                actions.Add("Could not determine USB device instance ID — skipping USB reset");
            }

            RecordRecovery();
            return actions;
        }

        // ==================== SHARED PRINTER RECOVERY ====================

        private async Task<List<string>> ExecuteSharedRecoveryAsync(PrinterConfig printer, List<string> actions)
        {
            string uncPath = printer.PortName;
            if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\"))
                uncPath = printer.Name;

            string server = ExtractServerName(uncPath);

            // Step 1: Cancel stuck jobs
            Logger.Info("Step 1/3: Cancelling stuck print jobs...");
            _spooler.CancelAllJobs(printer.Name);
            actions.Add("Cancelled stuck print jobs");
            await Task.Delay(_stepWaitSeconds * 1000);

            // Step 2: Clean stale spool files
            Logger.Info("Step 2/3: Cleaning stale spool files...");
            var cleaned = _fileCleaner.CleanStaleFiles(_StaleFileThreshold());
            actions.Add($"Cleaned {cleaned} stale spool files");
            await Task.Delay(_stepWaitSeconds * 1000);

            // Step 3: Disconnect and reconnect the shared printer
            Logger.Info($"Step 3/3: Resetting shared printer: {printer.Name}");

            // Disconnect existing mapping
            Logger.Info($"  Disconnecting: {uncPath}");
            RunHidden("net", $"use {uncPath} /delete /y");
            await Task.Delay(3000);

            // Also remove from printer store via printui
            if (!string.IsNullOrEmpty(server))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "rundll32.exe",
                        Arguments = $"printui.dll,PrintUIEntry /dl /n \"{printer.Name}\" /c \"{server}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi)?.WaitForExit(10000);
                }
                catch { /* Ignore errors — continue with reconnect */ }
            }

            await Task.Delay(_stepWaitSeconds * 1000);

            // Reconnect the mapping
            Logger.Info($"  Reconnecting: {uncPath}");
            RunHidden("net", $"use {uncPath} /persistent:yes");

            // Also re-add via printui (ensures it shows in Printers folder)
            if (!string.IsNullOrEmpty(server))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "rundll32.exe",
                        Arguments = $"printui.dll,PrintUIEntry /ga /n \"{uncPath}\" /c \"{server}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi)?.WaitForExit(15000);
                }
                catch { /* ignore */ }
            }

            await Task.Delay(_stepWaitSeconds * 1000);

            // Restart spooler to pick up reconnected printer
            Logger.Info("Restarting spooler after shared printer reconnect...");
            var spoolerOk = await _spooler.RestartAsync(_spoolerTimeoutSeconds);
            if (spoolerOk)
            {
                actions.Add($"Disconnected and reconnected shared printer ({uncPath})");
                actions.Add("Restarted Print Spooler (OK)");

                if (!IsPrinterStillBroken(printer))
                {
                    actions.Add("RESULT: Problem resolved after shared printer reconnect");
                    RecordRecovery();
                    return actions;
                }
            }
            else
            {
                actions.Add("Restarted Print Spooler (FAILED)");
            }

            RecordRecovery();
            return actions;
        }

        // ==================== HELPERS ====================

        /// <summary>
        /// Fast check: is the printer still in a bad state?
        /// </summary>
        private bool IsPrinterStillBroken(PrinterConfig printer)
        {
            var status = _spooler.GetStatus();
            if (status != ServiceControllerStatus.Running)
                return true;

            var jobs = _detector.GetProblematicJobs(new[] { printer.Name });
            if (jobs.Count > 0)
                return true;

            using (var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_Printer WHERE Name = '{printer.Name.Replace("'", "''")}'"))
            {
                foreach (ManagementObject printerMo in searcher.Get())
                {
                    var ps = (uint?)(printerMo["PrinterStatus"]) ?? 0;
                    var de = (uint?)(printerMo["DetectedErrorState"]) ?? 0;
                    if (ps == 3 || ps == 4 || ps == 7 || de > 0)
                        return true;
                }
            }

            return false;
        }

        private int _StaleFileThreshold()
        {
            return int.TryParse(
                ConfigurationManager.AppSettings["StaleFileThresholdSeconds"] ?? "300", out var s) ? s : 300;
        }

        private void RecordRecovery()
        {
            _lastRecoveryTime = DateTime.Now;
            _recoveryHistory.Add(DateTime.Now);
            _recoveryHistory.RemoveAll(t => (DateTime.Now - t).TotalHours >= 1);
            Logger.Info($"Recovery recorded. Total this hour: {RecoveriesThisHour}");
        }

        private static string ExtractServerName(string uncPath)
        {
            if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\"))
                return null;
            var parts = uncPath.TrimStart('\\').Split('\\');
            return parts.Length > 0 ? parts[0] : null;
        }

        private static void RunHidden(string fileName, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi)?.WaitForExit(10000);
            }
            catch { /* Ignore */ }
        }
    }
}