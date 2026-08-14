using System;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace PrintSpoolerGuardian
{
    static class Program
    {
        private static NotifyIcon _trayIcon;
        private static ContextMenuStrip _trayMenu;
        private static PrintMonitorService _monitorService;
        private static Thread _monitorThread;
        private static System.Windows.Forms.Timer _pauseTimer;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var logDir = ConfigurationManager.AppSettings["LogDirectory"] ?? @"C:\ProgramData\PrintSpoolerGuardian";
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            RegisterStartup();
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

        private static void RegisterStartup()
        {
            try
            {
                var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) { Logger.Debug("Not running as admin — skipping all-users startup registration"); return; }
                var startupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Startup");
                var shortcutPath = Path.Combine(startupDir, "Print Spooler Guardian.lnk");
                if (File.Exists(shortcutPath)) { Logger.Debug("Startup shortcut already exists"); return; }
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) throw new InvalidOperationException("WScript.Shell is unavailable.");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Process.GetCurrentProcess().MainModule.FileName });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Print Spooler Guardian — printer auto-recovery" });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell);
                Logger.Info("Startup shortcut created in: " + startupDir);
            }
            catch (Exception ex) { Logger.Warn("Could not register startup: " + ex.Message); }
        }

        private static void ExitClick(object sender, EventArgs e) { _trayIcon.Visible = false; _monitorService.RequestStop(); Application.Exit(); }
    }

    class GuardianApplicationContext : System.Windows.Forms.ApplicationContext { }
}
