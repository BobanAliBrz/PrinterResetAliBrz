using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace PrintSpoolerGuardian.Installer
{
    public class InstallerForm : Form
    {
        private RichTextBox _logBox;
        private Button _installBtn;
        private Button _uninstallBtn;
        private Button _exitBtn;
        private ProgressBar _progress;
        private Label _statusLabel;
        private Label _titleLabel;

        private const string DefaultRepo = "YOURORG/PrintSpoolerGuardian";
        private const string ServiceName = "PrintSpoolerGuardian";
        private const string LocalInstallDir = @"C:\ProgramData\PrintSpoolerGuardian";

        public InstallerForm()
        {
            Text = "Print Spooler Guardian — Installer";
            Size = new Size(640, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            Icon = SystemIcons.Shield;

            BuildUI();
            Log("Welcome to Print Spooler Guardian Installer");
            Log($"Version: {FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion}");
            Log($"Machine: {Environment.MachineName}");
            Log($"OS: {Environment.OSVersion.VersionString}");
            Log("");
            CheckPrerequisites();
        }

        private void BuildUI()
        {
            _titleLabel = new Label
            {
                Text = "🖨 Print Spooler Guardian",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                Location = new Point(20, 15),
                AutoSize = true
            };

            var subtitle = new Label
            {
                Text = "Automated installer — detects USB printers and fixes stuck print jobs",
                Location = new Point(20, 45),
                AutoSize = true,
                ForeColor = Color.Gray
            };

            _statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(20, 80),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Italic)
            };

            _progress = new ProgressBar
            {
                Location = new Point(20, 105),
                Size = new Size(580, 23),
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous
            };

            _logBox = new RichTextBox
            {
                Location = new Point(20, 140),
                Size = new Size(580, 260),
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Font = new Font("Consolas", 8.5f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.LightGray
            };

            _installBtn = new Button
            {
                Text = "Install / Update",
                Location = new Point(20, 415),
                Size = new Size(180, 35),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _installBtn.FlatAppearance.BorderSize = 0;
            _installBtn.Click += InstallBtn_Click;

            _uninstallBtn = new Button
            {
                Text = "Uninstall",
                Location = new Point(210, 415),
                Size = new Size(140, 35),
                BackColor = Color.FromArgb(180, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _uninstallBtn.FlatAppearance.BorderSize = 0;
            _uninstallBtn.Click += UninstallBtn_Click;

            _exitBtn = new Button
            {
                Text = "Exit",
                Location = new Point(510, 415),
                Size = new Size(90, 35),
                FlatStyle = FlatStyle.Flat
            };
            _exitBtn.Click += (s, e) => Close();

            Controls.AddRange(new Control[] {
                _titleLabel, subtitle, _statusLabel, _progress,
                _logBox, _installBtn, _uninstallBtn, _exitBtn
            });
        }

        private void Log(string msg)
        {
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            _logBox.ScrollToCaret();
        }

        private void SetStatus(string status, int progress = -1)
        {
            _statusLabel.Text = status;
            if (progress >= 0) _progress.Value = Math.Min(100, progress);
        }

        private void CheckPrerequisites()
        {
            Log("Checking prerequisites...");

            // Check .NET Framework 4.8
            var net48 = IsNet48Installed();
            if (net48)
                Log("  ✓ .NET Framework 4.8 detected");
            else
                Log("  ✗ .NET Framework 4.8 NOT found — will install");

            // Check if already installed (look for startup shortcut or exe)
            var startupShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                "Print Spooler Guardian.lnk");
            var exePath = Path.Combine(LocalInstallDir, "PrintSpoolerGuardian.exe");

            if (File.Exists(startupShortcut) || File.Exists(exePath))
            {
                Log("  ✓ Existing installation detected");
                if (File.Exists(exePath))
                    Log($"  ✓ Version: {GetFileVersion(exePath)}");
            }
            else
            {
                Log("  → Not installed yet");
            }

            // Also check for old service (migration)
            if (ServiceExists())
                Log("  ⚠ Old Windows Service found — will be removed during install");
        }

        private bool IsNet48Installed()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    var release = key?.GetValue("Release") as int? ?? 0;
                    return release >= 528449; // .NET 4.8 release key
                }
            }
            catch
            {
                return false;
            }
        }

        private string GetLatestReleaseUrl()
        {
            try
            {
                var repo = DefaultRepo;
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.config");
                if (File.Exists(configPath))
                {
                    var doc = new XmlDocument();
                    doc.Load(configPath);
                    var node = doc.SelectSingleNode("//add[@key='UpdateGitHubRepo']");
                    if (node?.Attributes?["value"]?.Value is string val && !string.IsNullOrEmpty(val))
                        repo = val;
                }

                // Use GitHub API to get latest release
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "PrintSpoolerGuardian-Installer");
                    var apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
                    var json = wc.DownloadString(apiUrl);

                    // Simple JSON parsing (no dependency on Newtonsoft)
                    var match = Regex.Match(json, @"""browser_download_url""\s*:\s*""([^""]*\.zip)""");
                    if (match.Success)
                        return match.Groups[1].Value;

                    Log("  ⚠ Could not parse latest release URL from GitHub API");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log($"  ⚠ Could not check GitHub releases: {ex.Message}");
                return null;
            }
        }

        private async void InstallBtn_Click(object sender, EventArgs e)
        {
            _installBtn.Enabled = false;
            _uninstallBtn.Enabled = false;

            try
            {
                // Step 1: Install .NET 4.8 if needed
                if (!IsNet48Installed())
                {
                    Log("Installing .NET Framework 4.8...");
                    SetStatus("Installing .NET Framework 4.8...", 5);

                    var ndpPath = Path.Combine(Path.GetTempPath(), "ndp48-web.exe");
                    using (var wc = new WebClient())
                    {
                        wc.DownloadFile("https://go.microsoft.com/fwlink/?linkid=2088631", ndpPath);
                    }

                    Log("  Running .NET installer (may require reboot)...");
                    var psi = new ProcessStartInfo
                    {
                        FileName = ndpPath,
                        Arguments = "/q /norestart",
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    var proc = Process.Start(psi);
                    proc.WaitForExit();

                    if (proc.ExitCode == 3010)
                    {
                        Log("  ⚠ Reboot required for .NET installation. Please reboot and re-run installer.");
                        SetStatus("Reboot needed for .NET 4.8", 0);
                        _installBtn.Enabled = true;
                        return;
                    }

                    if (!IsNet48Installed())
                    {
                        Log("  ✗ Failed to install .NET Framework 4.8");
                        SetStatus("Failed: .NET 4.8 not installed", 0);
                        _installBtn.Enabled = true;
                        return;
                    }
                    Log("  ✓ .NET Framework 4.8 installed");
                }

                // Step 2: Download latest release
                SetStatus("Checking for latest release...", 10);
                Log("Fetching latest GitHub release...");

                var downloadUrl = GetLatestReleaseUrl();
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    // Fallback: check if local build already exists
                    var exePath = Path.Combine(LocalInstallDir, "PrintSpoolerGuardian.exe");
                    if (File.Exists(exePath))
                    {
                        Log("No newer release found online, but existing installation detected. Using local files.");
                        downloadUrl = null;
                    }
                    else
                    {
                        Log("ERROR: Could not find a release. Please download manually from GitHub Releases.");
                        SetStatus("Release download failed", 0);
                        _installBtn.Enabled = true;
                        return;
                    }
                }

                var zipPath = Path.Combine(Path.GetTempPath(), "psg_latest.zip");
                if (downloadUrl != null)
                {
                    Log($"Downloading: {Path.GetFileName(downloadUrl)}");
                    SetStatus("Downloading release...", 20);

                    using (var wc = new WebClient())
                    {
                        wc.DownloadProgressChanged += (s, ev) =>
                        {
                            SetStatus($"Downloading... {ev.ProgressPercentage}%", ev.ProgressPercentage / 2 + 20);
                        };
                        wc.DownloadFileCompleted += (s, ev) =>
                        {
                            if (ev.Error != null) Log($"Download failed: {ev.Error.Message}");
                        };
                        await wc.DownloadFileTaskAsync(new Uri(downloadUrl), zipPath);
                    }

                    Log("Download complete. Extracting...");
                }

                // Step 3: Create install directory
                SetStatus("Installing files...", 50);
                Directory.CreateDirectory(LocalInstallDir);

                if (downloadUrl != null && File.Exists(zipPath))
                {
                    try { ZipFile.ExtractToDirectory(zipPath, LocalInstallDir, true); }
                    catch (Exception ex)
                    {
                        // Try 7z-free extraction using System.IO.Compression
                        // If zip extraction fails, try extracting what we can
                        Log($"  ⚠ Zip extraction warning: {ex.Message}");
                        using (var archive = ZipFile.OpenRead(zipPath))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                try
                                {
                                    var destPath = Path.Combine(LocalInstallDir, entry.FullName);
                                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                                    if (!entry.FullName.EndsWith("/"))
                                        entry.ExtractToFile(destPath, true);
                                }
                                catch { /* Skip problematic entries */ }
                            }
                        }
                    }
                    File.Delete(zipPath);
                }

                // Step 4: Find the main executable
                var exeFullPath = Path.Combine(LocalInstallDir, "PrintSpoolerGuardian.exe");
                if (!File.Exists(exeFullPath))
                {
                    // Try to find exe in subdirectory (zip may have a folder)
                    var exes = Directory.GetFiles(LocalInstallDir, "PrintSpoolerGuardian.exe", SearchOption.AllDirectories);
                    if (exes.Length > 0)
                    {
                        File.Copy(exes[0], exeFullPath, true);
                        if (exes[0] != exeFullPath) File.Delete(exes[0]);
                    }
                    else
                    {
                        Log("ERROR: Could not find PrintSpoolerGuardian.exe in downloaded files.");
                        Log("Please check the GitHub release contents.");
                        SetStatus("Installation failed", 0);
                        _installBtn.Enabled = true;
                        return;
                    }
                }

                // Step 5: Clean up old service installation if present (migration)
                try
                {
                    var existingServices = System.ServiceProcess.ServiceController.GetServices();
                    if (existingServices.Any(s => s.ServiceName == ServiceName))
                    {
                        Log("Removing old Windows Service installation...");
                        // Stop it first
                        using (var sc = new System.ServiceProcess.ServiceController(ServiceName))
                        {
                            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                            {
                                sc.Stop();
                                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                            }
                        }
                        Process.Start(new ProcessStartInfo("sc.exe", $"delete {ServiceName}")
                        { UseShellExecute = true, Verb = "runas", CreateNoWindow = true })?.WaitForExit(10000);
                        await Task.Delay(1000);
                        Log("  ✓ Old service removed");
                    }
                }
                catch { /* No existing service */ }

                // Step 6: Register in All Users Startup folder
                SetStatus("Registering auto-start...", 80);
                Log("Registering for auto-start on all users...");
                try
                {
                    var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                    var shortcutPath = Path.Combine(startupDir, "Print Spooler Guardian.lnk");

                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = exeFullPath;
                    shortcut.Description = "Print Spooler Guardian — printer auto-recovery";
                    shortcut.WorkingDirectory = LocalInstallDir;
                    shortcut.Save();
                    Marshal.FinalReleaseComObject(shortcut);
                    Marshal.FinalReleaseComObject(shell);

                    Log("  ✓ Startup shortcut created: " + shortcutPath);
                }
                catch (Exception ex)
                {
                    Log($"  ✗ Failed to create startup shortcut: {ex.Message}");
                }

                // Step 7: Launch the app now
                SetStatus("Starting...", 90);
                Log("Starting Print Spooler Guardian...");
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exeFullPath,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    Log("  ✓ Launched successfully");
                }
                catch (Exception ex)
                {
                    Log($"  ⚠ Could not launch: {ex.Message} (will start on next logon)");
                }

                // Step 8: Create desktop shortcut
                SetStatus("Creating shortcuts...", 95);
                CreateDesktopShortcut();

                SetStatus("Installation complete!", 100);
                Log("");
                Log("============================================");
                Log("  ✓ INSTALLATION COMPLETE");
                Log($"  App running: {LocalInstallDir}\\PrintSpoolerGuardian.exe");
                Log($"  Auto-start: All Users Startup folder (every user)");
                Log($"  Log file: {Path.Combine(LocalInstallDir, "PrintSpoolerGuardian.log")}");
                Log($"  Tray icon: bottom-right system tray");
                Log("============================================");
                Log("");
                Log("Print Spooler Guardian is now running.");
                Log("It will auto-start for every user at logon.");
            }
            finally
            {
                _installBtn.Enabled = true;
                _uninstallBtn.Enabled = true;
            }
        }

        private void UninstallBtn_Click(object sender, EventArgs e)
        {
            var confirm = MessageBox.Show("Remove Print Spooler Guardian completely?" + Environment.NewLine +
                "This will: stop the app, remove the startup registration, and delete all files.",
                "Confirm Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            _installBtn.Enabled = false;
            _uninstallBtn.Enabled = false;

            Log("Uninstalling...");

            // Remove All Users Startup shortcut
            try
            {
                var startupShortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                    "Print Spooler Guardian.lnk");
                if (File.Exists(startupShortcut)) File.Delete(startupShortcut);
                Log("  ✓ Startup shortcut removed");
            }
            catch (Exception ex)
            {
                Log($"  ⚠ Could not remove startup shortcut: {ex.Message}");
            }

            // Remove desktop shortcut
            try
            {
                var desktopShortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "Print Spooler Guardian.lnk");
                if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
            }
            catch { /* ignore */ }

            // Try to stop any running instance
            try
            {
                foreach (var proc in Process.GetProcessesByName("PrintSpoolerGuardian"))
                {
                    proc.Kill();
                    Log("  ✓ Stopped running instance");
                }
            }
            catch { Log("  ⚠ Could not stop running instance"); }

            // Remove install directory
            try
            {
                if (Directory.Exists(LocalInstallDir))
                {
                    // Retry a few times in case files are locked
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            Directory.Delete(LocalInstallDir, true);
                            break;
                        }
                        catch
                        {
                            if (i < 2) System.Threading.Thread.Sleep(2000);
                            else throw;
                        }
                    }
                    Log($"  ✓ Removed {LocalInstallDir}");
                }
            }
            catch (Exception ex)
            {
                Log($"  ⚠ Could not remove files (may be in use): {ex.Message}");
                Log("  Please delete manually: " + LocalInstallDir);
            }

            Log("");
            Log("  Uninstallation complete.");
            SetStatus("Uninstalled", 0);
            _installBtn.Enabled = true;
            _uninstallBtn.Enabled = true;
        }

        private bool ServiceExists()
        {
            try
            {
                return System.ServiceProcess.ServiceController.GetServices()
                    .Any(s => s.ServiceName == ServiceName);
            }
            catch
            {
                return false;
            }
        }

        private string GetFileVersion(string path)
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private void CreateDesktopShortcut()
        {
            try
            {
                var shortcutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "Print Spooler Guardian.lnk");

                // Use WScript.Shell COM object for shortcut creation
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = Path.Combine(LocalInstallDir, "PrintSpoolerGuardian.exe");
                shortcut.Description = "View Print Spooler Guardian status";
                shortcut.IconLocation = Path.Combine(LocalInstallDir, "PrintSpoolerGuardian.exe,0");
                shortcut.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);

                Log($"  ✓ Desktop shortcut created");
            }
            catch (Exception ex)
            {
                Log($"  ⚠ Could not create shortcut: {ex.Message}");
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
        }
    }
}