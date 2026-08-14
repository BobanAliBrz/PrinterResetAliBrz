using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading;

namespace PrintSpoolerGuardian
{
    /// <summary>Runs conservative USB and shared-printer recovery cycles.</summary>
    public class RecoveryEngine
    {
        private readonly SpoolerController _spooler = new SpoolerController();
        private readonly UsbPrinterResetter _usbResetter = new UsbPrinterResetter();
        private readonly StaleFileCleaner _fileCleaner = new StaleFileCleaner();
        private readonly PrintJobDetector _detector = new PrintJobDetector();
        private readonly List<DateTime> _recoveryHistory = new List<DateTime>();
        private DateTime _lastRecoveryTime = DateTime.MinValue;
        private readonly int _cooldownMinutes;
        private readonly int _maxRecoveriesPerHour;
        private readonly int _spoolerTimeoutSeconds;
        private readonly int _usbResetWaitSeconds;
        private readonly int _stepWaitSeconds;

        public RecoveryEngine()
        {
            _cooldownMinutes = GetSetting("CooldownMinutes", 10);
            _maxRecoveriesPerHour = GetSetting("MaxRecoveriesPerHour", 3);
            _spoolerTimeoutSeconds = GetSetting("SpoolerTimeoutSeconds", 60);
            _usbResetWaitSeconds = GetSetting("UsbResetWaitSeconds", 15);
            _stepWaitSeconds = GetSetting("StepWaitSeconds", 5);
        }

        public bool IsInCooldown { get { return (DateTime.Now - _lastRecoveryTime).TotalMinutes < _cooldownMinutes; } }
        public int RecoveriesThisHour { get { return _recoveryHistory.Count(t => (DateTime.Now - t).TotalHours < 1); } }
        public bool IsRateLimited { get { return RecoveriesThisHour >= _maxRecoveriesPerHour; } }

        public List<string> ExecuteRecovery(PrinterConfig printer)
        {
            var actions = new List<string>();
            if (IsInCooldown) { actions.Add("SKIP: In cooldown (" + _cooldownMinutes + " min since last recovery)"); return actions; }
            if (IsRateLimited) { actions.Add("SKIP: Rate limited (" + RecoveriesThisHour + "/" + _maxRecoveriesPerHour + " this hour)"); return actions; }
            Logger.Info("=== Starting recovery for: " + printer.Name + " [" + printer.ConnectionType + "] ===");
            return printer.IsUsb ? ExecuteUsbRecovery(printer, actions) : ExecuteSharedRecovery(printer, actions);
        }

        private List<string> ExecuteUsbRecovery(PrinterConfig printer, List<string> actions)
        {
            Logger.Info("Step 1/4: Cancelling stuck print jobs...");
            _spooler.CancelAllJobs(printer.Name); actions.Add("Cancelled stuck print jobs"); WaitStep();
            Logger.Info("Step 2/4: Cleaning stale spool files...");
            var cleaned = _fileCleaner.CleanStaleFiles(GetSetting("StaleFileThresholdSeconds", 300));
            actions.Add("Cleaned " + cleaned + " stale spool files"); WaitStep();
            Logger.Info("Step 3/4: Restarting Print Spooler...");
            if (_spooler.Restart(_spoolerTimeoutSeconds))
            {
                actions.Add("Restarted Print Spooler (OK)"); WaitStep();
                if (!IsPrinterStillBroken(printer))
                {
                    Logger.Info("Recovery successful after spooler restart.");
                    actions.Add("RESULT: Problem resolved after spooler restart"); RecordRecovery(); return actions;
                }
            }
            else actions.Add("Restarted Print Spooler (FAILED)");

            Logger.Info("Step 4/4: Resetting USB device...");
            var deviceId = _detector.GetUsbDeviceInstanceId(printer.PortName);
            if (string.IsNullOrEmpty(deviceId)) actions.Add("Could not determine USB device instance ID — skipping USB reset");
            else if (_usbResetter.Reset(deviceId, _usbResetWaitSeconds))
            {
                actions.Add("Reset USB device: " + deviceId); WaitStep();
                if (_spooler.Restart(_spoolerTimeoutSeconds))
                {
                    actions.Add("Restarted Print Spooler post-USB-reset (OK)");
                    actions.Add("RESULT: Full recovery (USB reset + spooler restart)");
                }
                else actions.Add("Restarted Print Spooler post-USB-reset (FAILED)");
            }
            else actions.Add("USB device reset FAILED: " + deviceId);
            RecordRecovery(); return actions;
        }

        private List<string> ExecuteSharedRecovery(PrinterConfig printer, List<string> actions)
        {
            string uncPath = printer.PortName;
            if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\")) uncPath = printer.Name;
            string server = ExtractServerName(uncPath);
            Logger.Info("Step 1/3: Cancelling stuck print jobs...");
            _spooler.CancelAllJobs(printer.Name); actions.Add("Cancelled stuck print jobs"); WaitStep();
            Logger.Info("Step 2/3: Cleaning stale spool files...");
            var cleaned = _fileCleaner.CleanStaleFiles(GetSetting("StaleFileThresholdSeconds", 300));
            actions.Add("Cleaned " + cleaned + " stale spool files"); WaitStep();
            Logger.Info("Step 3/3: Resetting shared printer: " + printer.Name);
            RunHidden("net", "use " + uncPath + " /delete /y"); Thread.Sleep(3000);
            if (!string.IsNullOrEmpty(server)) RunHidden("rundll32.exe", "printui.dll,PrintUIEntry /dl /n \"" + printer.Name + "\" /c \"" + server + "\"");
            WaitStep(); RunHidden("net", "use " + uncPath + " /persistent:yes");
            if (!string.IsNullOrEmpty(server)) RunHidden("rundll32.exe", "printui.dll,PrintUIEntry /ga /n \"" + uncPath + "\" /c \"" + server + "\"");
            WaitStep(); Logger.Info("Restarting spooler after shared printer reconnect...");
            if (_spooler.Restart(_spoolerTimeoutSeconds))
            {
                actions.Add("Disconnected and reconnected shared printer (" + uncPath + ")"); actions.Add("Restarted Print Spooler (OK)");
                if (!IsPrinterStillBroken(printer)) { actions.Add("RESULT: Problem resolved after shared printer reconnect"); RecordRecovery(); return actions; }
            }
            else actions.Add("Restarted Print Spooler (FAILED)");
            RecordRecovery(); return actions;
        }

        private bool IsPrinterStillBroken(PrinterConfig printer)
        {
            if (_spooler.GetStatus() != ServiceControllerStatus.Running || _detector.GetProblematicJobs(new[] { printer.Name }).Count > 0) return true;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer WHERE Name = '" + printer.Name.Replace("'", "''") + "'"))
            foreach (ManagementObject mo in searcher.Get())
            {
                uint ps = mo["PrinterStatus"] == null ? 0 : Convert.ToUInt32(mo["PrinterStatus"]);
                uint de = mo["DetectedErrorState"] == null ? 0 : Convert.ToUInt32(mo["DetectedErrorState"]);
                if (ps == 3 || ps == 4 || ps == 7 || de > 0) return true;
            }
            return false;
        }

        private static int GetSetting(string key, int fallback) { int value; return int.TryParse(ConfigurationManager.AppSettings[key], out value) ? value : fallback; }
        private void WaitStep() { Thread.Sleep(_stepWaitSeconds * 1000); }
        private void RecordRecovery() { _lastRecoveryTime = DateTime.Now; _recoveryHistory.Add(DateTime.Now); _recoveryHistory.RemoveAll(t => (DateTime.Now - t).TotalHours >= 1); Logger.Info("Recovery recorded. Total this hour: " + RecoveriesThisHour); }
        private static string ExtractServerName(string uncPath) { if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\")) return null; var parts = uncPath.TrimStart('\\').Split('\\'); return parts.Length > 0 ? parts[0] : null; }
        private static void RunHidden(string fileName, string arguments) { try { var psi = new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }; var p = Process.Start(psi); if (p != null) p.WaitForExit(15000); } catch { } }
    }
}
