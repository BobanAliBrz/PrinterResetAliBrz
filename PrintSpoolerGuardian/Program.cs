using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrintSpoolerGuardian
{
    static class Program
    {
        private static NotifyIcon _trayIcon;
        private static ContextMenuStrip _trayMenu;
        private static PrintMonitorService _monitorService;
        private static CancellationTokenSource _cts;
        private static bool _showWindow = false;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _cts = new CancellationTokenSource();

            // Ensure log directory exists
            var logDir = ConfigurationManager.AppSettings["LogDirectory"] ?? @"C:\ProgramData\PrintSpoolerGuardian";
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            // Start the monitoring service
            _monitorService = new PrintMonitorService();
            var monitorTask = Task.Run(() => _monitorService.RunAsync(_cts.Token));

            // Setup tray icon
            SetupTrayIcon();

            Application.Run(new ApplicationContext());

            // Cleanup
            _cts.Cancel();
            _monitorService?.StopAsync().Wait();
            monitorTask.Wait(5000);
        }

        private static void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Show Status", null, ShowStatusClick);
            _trayMenu.Items.Add("Run Recovery Now", null, RunRecoveryClick);
            _trayMenu.Items.Add("Pause Monitoring (30min)", null, PauseMonitoringClick);
            _trayMenu.Items.Add("Exit", null, ExitClick);

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                ContextMenuStrip = _trayMenu,
                Visible = true,
                Text = "Print Spooler Guardian"
            };
            _trayIcon.DoubleClick += (s, e) => ShowStatusClick(s, e);

            var balloonThread = new Thread(ShowBalloonThread)
            {
                IsBackground = true
            };
            balloonThread.Start();
        }

        private static void ShowBalloonThread()
        {
            Thread.Sleep(2000);
            _trayIcon.ShowBalloonTip(3000, "Print Spooler Guardian",
                "Monitoring started. Click for status.", ToolTipIcon.Info);
        }

        private static void ShowStatusClick(object sender, EventArgs e)
        {
            try
            {
                var status = _monitorService?.GetStatus();
                var msg = status != null
                    ? $"Status: {status}\n\nLast Check: {_monitorService.LastCheckTime:HH:mm:ss}\nRecoveries This Hour: {_monitorService.RecoveriesThisHour}\nUptime: {GetUptime()}"
                    : "Service not initialized yet.";
                MessageBox.Show(msg, "Print Spooler Guardian - Status",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string GetUptime()
        {
            // Simple uptime based on process start
            var uptime = DateTime.Now - Process.GetCurrentProcess().StartTime;
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
        }

        private static void RunRecoveryClick(object sender, EventArgs e)
        {
            Task.Run(async () =>
            {
                _trayIcon.ShowBalloonTip(2000, "Print Spooler Guardian",
                    "Running recovery now...", ToolTipIcon.Warning);
                await _monitorService?.ForceRecoveryAsync();
                _trayIcon.ShowBalloonTip(2000, "Print Spooler Guardian",
                    "Recovery complete. Check log for details.", ToolTipIcon.Info);
            });
        }

        private static DateTime _pauseUntil = DateTime.MinValue;

        private static void PauseMonitoringClick(object sender, EventArgs e)
        {
            _pauseUntil = DateTime.Now.AddMinutes(30);
            _trayIcon.ShowBalloonTip(2000, "Print Spooler Guardian",
                "Monitoring paused for 30 minutes.", ToolTipIcon.Warning);

            var menuItem = _trayMenu.Items["Pause Monitoring (30min)"];
            menuItem.Enabled = false;

            Task.Delay(TimeSpan.FromMinutes(30)).ContinueWith(_ =>
            {
                menuItem.Enabled = true;
            });
        }

        private static void ExitClick(object sender, EventArgs e)
        {
            _trayIcon.Visible = false;
            _cts.Cancel();
            Application.Exit();
        }
    }
}

// Minimal ApplicationContext to keep tray alive
namespace PrintSpoolerGuardian
{
    using System.Windows.Forms;
    class ApplicationContext : System.Windows.Forms.ApplicationContext
    {
        public ApplicationContext() { }
    }
}