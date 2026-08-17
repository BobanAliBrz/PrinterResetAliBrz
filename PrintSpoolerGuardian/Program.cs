using System;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace PrintSpoolerGuardian
{
    static class Program
    {
        internal const string StartupTaskName = "Print Spooler Guardian";
        private const string ScheduledLaunchArgument = "/scheduled";
        private static NotifyIcon _trayIcon;
        private static ContextMenuStrip _trayMenu;
        private static PrintMonitorService _monitorService;
        private static Thread _monitorThread;
        private static System.Windows.Forms.Timer _pauseTimer;

        [STAThread]
        static void Main(string[] args)
        {
            // The scheduled task is registered with the highest available
            // privilege level.  A direct Explorer launch stays UAC-free and
            // asks that task to start the privileged tray instance instead.
            if (!WasStartedByScheduledTask(args) && !IsAdministrator())
            {
                if (TryStartScheduledInstance()) return;
                Logger.Warn("Elevated startup task was unavailable; continuing without elevation.");
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var logDir = ConfigurationManager.AppSettings["LogDirectory"] ?? @"C:\ProgramData\PrintSpoolerGuardian";
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            _monitorService = new PrintMonitorService();
            _monitorThread = new Thread(_monitorService.Run) { IsBackground = true, Name = "Print Spooler Guardian monitor" };
            _monitorThread.Start();
            SetupTrayIcon();
            Application.Run(new GuardianApplicationContext());
            _monitorService.RequestStop();
            _monitorThread.Join(5000);
        }

        private static void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Show Status", null, ShowStatusClick);
            _trayMenu.Items.Add("Run Recovery Now", null, RunRecoveryClick);
            _trayMenu.Items.Add("Pause Monitoring (30min)", null, PauseMonitoringClick);
            _trayMenu.Items.Add("Exit", null, ExitClick);
            _trayIcon = new NotifyIcon { Icon = IconHelper.CreatePrinterIcon(), ContextMenuStrip = _trayMenu, Visible = true, Text = "Print Spooler Guardian" };
            _trayIcon.DoubleClick += ShowStatusClick;
            var balloonThread = new Thread(delegate() { Thread.Sleep(2000); _trayIcon.ShowBalloonTip(3000, "Print Spooler Guardian", "Monitoring started. Click for status.", ToolTipIcon.Info); }) { IsBackground = true };
            balloonThread.Start();
        }

        private static void ShowStatusClick(object sender, EventArgs e)
        {
            try
            {
                string status = _monitorService == null ? "Service not initialized yet." : _monitorService.GetStatus();
                MessageBox.Show("Status: " + status + "\n\nLast Check: " + _monitorService.LastCheckTime.ToString("HH:mm:ss") + "\nRecoveries This Hour: " + _monitorService.RecoveriesThisHour + "\nUptime: " + GetUptime(), "Print Spooler Guardian - Status", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private static string GetUptime()
        {
            var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
            return (int)uptime.TotalHours + "h " + uptime.Minutes + "m " + uptime.Seconds + "s";
        }

        private static void RunRecoveryClick(object sender, EventArgs e)
        {
            var recoveryThread = new Thread(delegate()
            {
                _trayIcon.ShowBalloonTip(2000, "Print Spooler Guardian", "Running recovery now...", ToolTipIcon.Warning);
                _monitorService.ForceRecovery();
                _trayIcon.ShowBalloonTip(2000, "Print Spooler Guardian", "Recovery complete. Check log for details.", ToolTipIcon.Info);
            }) { IsBackground = true, Name = "Manual printer recovery" };
            recoveryThread.Start();
        }

        private static void PauseMonitoringClick(object sender, EventArgs e)
        {
            _monitorService.Pause(30);
            _trayIcon.ShowBalloonTip(2000, "Print Spooler Guardian", "Monitoring paused for 30 minutes.", ToolTipIcon.Warning);
            _trayMenu.Items[2].Enabled = false;
            _pauseTimer = new System.Windows.Forms.Timer();
            _pauseTimer.Interval = 30 * 60 * 1000;
            _pauseTimer.Tick += delegate { _pauseTimer.Stop(); _pauseTimer.Dispose(); _pauseTimer = null; _trayMenu.Items[2].Enabled = true; };
            _pauseTimer.Start();
        }

        private static bool WasStartedByScheduledTask(string[] args)
        {
            if (args == null) return false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, ScheduledLaunchArgument, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsAdministrator()
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool TryStartScheduledInstance()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    Arguments = "/Run /TN \"" + StartupTaskName + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process taskScheduler = Process.Start(startInfo))
                {
                    taskScheduler.WaitForExit(5000);
                    if (!taskScheduler.HasExited || taskScheduler.ExitCode == 0)
                    {
                        Logger.Info("Requested elevated tray instance from Task Scheduler.");
                        return true;
                    }
                    Logger.Warn("Task Scheduler returned exit code " + taskScheduler.ExitCode + " while starting the tray instance.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not start the elevated startup task: " + ex.Message);
            }
            return false;
        }

        private static void ExitClick(object sender, EventArgs e) { _trayIcon.Visible = false; _monitorService.RequestStop(); Application.Exit(); }
    }

    class GuardianApplicationContext : System.Windows.Forms.ApplicationContext { }
}
