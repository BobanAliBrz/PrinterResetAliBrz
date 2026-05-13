using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Checks GitHub releases periodically and auto-updates the application.
    /// Designed to run alongside the monitor service with minimal overhead.
    /// </summary>
    public class AutoUpdater
    {
        private readonly string _updateRepo;
        private readonly int _checkIntervalHours;
        private readonly string _installDirectory;
        private readonly string _currentVersion;

        private Timer _checkTimer;
        private DateTime _lastCheck = DateTime.MinValue;

        public AutoUpdater()
        {
            _updateRepo = ConfigurationManager.AppSettings["UpdateGitHubRepo"] ?? "";
            _checkIntervalHours = int.TryParse(
                ConfigurationManager.AppSettings["UpdateCheckIntervalHours"] ?? "24", out var h) ? h : 24;
            _installDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory);
            _currentVersion = GetCurrentVersion();
        }

        /// <summary>
        /// Returns true if auto-updates are configured.
        /// </summary>
        public bool IsEnabled => !string.IsNullOrEmpty(_updateRepo) && _checkIntervalHours > 0;

        public string LastCheckResult { get; private set; } = "Not checked yet";
        public DateTime LastCheckTime => _lastCheck;

        /// <summary>
        /// Starts the periodic update check timer.
        /// </summary>
        public void Start()
        {
            if (!IsEnabled)
            {
                Logger.Debug("Auto-update disabled (no GitHub repo configured or interval is 0)");
                return;
            }

            // Initial check after 5 minutes, then on interval
            _checkTimer = new Timer(async _ => await CheckForUpdatesAsync(), null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromHours(_checkIntervalHours));

            Logger.Info($"Auto-update enabled. Repository: {_updateRepo}, Check interval: {_checkIntervalHours}h");
        }

        public void Stop()
        {
            _checkTimer?.Dispose();
            _checkTimer = null;
        }

        /// <summary>
        /// Performs an immediate update check.
        /// </summary>
        public async Task CheckNowAsync()
        {
            await CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            if (!IsEnabled) return;

            _lastCheck = DateTime.Now;

            try
            {
                Logger.Info("Checking for updates...");

                var downloadUrl = await GetLatestReleaseUrlAsync();
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    LastCheckResult = "Could not retrieve latest release info";
                    Logger.Warn(LastCheckResult);
                    return;
                }

                var latestVersion = ExtractVersionFromUrl(downloadUrl);
                if (string.IsNullOrEmpty(latestVersion))
                {
                    LastCheckResult = "Could not determine version from release URL";
                    Logger.Warn(LastCheckResult);
                    return;
                }

                var comparison = CompareVersions(_currentVersion, latestVersion);

                if (comparison < 0)
                {
                    Logger.Info($"New version available: {latestVersion} (current: {_currentVersion})");
                    LastCheckResult = $"Update available: {latestVersion}";

                    // Download and apply update
                    var zipPath = Path.Combine(Path.GetTempPath(), $"psg_update_{latestVersion}.zip");
                    try
                    {
                        using (var wc = new WebClient())
                        {
                            wc.Headers.Add("User-Agent", "PrintSpoolerGuardian-AutoUpdater");
                            await wc.DownloadFileTaskAsync(new Uri(downloadUrl), zipPath);
                        }

                        // Extract to install directory
                        try
                        {
                            ZipFile.ExtractToDirectory(zipPath, _installDirectory, true);
                        }
                        catch
                        {
                            // Fallback: manual entry-by-entry extraction
                            using (var archive = ZipFile.OpenRead(zipPath))
                            {
                                foreach (var entry in archive.Entries)
                                {
                                    try
                                    {
                                        // Only extract core files
                                        if (!entry.FullName.EndsWith(".exe") &&
                                            !entry.FullName.EndsWith(".config") &&
                                            !entry.FullName.EndsWith(".dll") &&
                                            !entry.FullName.EndsWith(".manifest") &&
                                            !entry.FullName.EndsWith(".pdb"))
                                            continue;

                                        var destPath = Path.Combine(_installDirectory,
                                            Path.GetFileName(entry.FullName));
                                        if (!entry.FullName.EndsWith("/"))
                                            entry.ExtractToFile(destPath, true);
                                    }
                                    catch { /* Skip problematic files */ }
                                }
                            }
                        }

                        File.Delete(zipPath);

                        // Restart the service to pick up new binary
                        Logger.Info("Update downloaded. Restarting Print Spooler Guardian service...");
                        _checkTimer?.Dispose();
                        RestartSelf();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Auto-update failed: {ex.Message}");
                        LastCheckResult = $"Update failed: {ex.Message}";
                        File.Delete(zipPath);
                    }
                }
                else
                {
                    Logger.Info($"No update needed. Current: {_currentVersion}, Latest: {latestVersion}");
                    LastCheckResult = $"Up to date (v{_currentVersion})";
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Update check failed: {ex.Message}");
                LastCheckResult = $"Check failed: {ex.Message}";
            }
        }

        private async Task<string> GetLatestReleaseUrlAsync()
        {
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "PrintSpoolerGuardian-AutoUpdater");
                    var apiUrl = $"https://api.github.com/repos/{_updateRepo}/releases/latest";
                    var json = await wc.DownloadStringTaskAsync(apiUrl);

                    // Simple regex-based JSON parsing (no Newtonsoft dependency)
                    var match = Regex.Match(json,
                        @"""browser_download_url""\s*:\s*""([^""]*(?:\.zip|\.msi))""");
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"GitHub API query failed: {ex.Message}");
            }

            return null;
        }

        private string ExtractVersionFromUrl(string url)
        {
            // Try to extract version from filename like PrintSpoolerGuardian_v1.2.3.zip
            var match = Regex.Match(url, @"[\._]v?(\d+\.\d+(?:\.\d+)*)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private string GetCurrentVersion()
        {
            try
            {
                var exePath = Path.Combine(_installDirectory, "PrintSpoolerGuardian.exe");
                if (File.Exists(exePath))
                    return FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "0.0.0";
            }
            catch { /* ignore */ }

            try
            {
                return FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location)
                    .ProductVersion ?? "0.0.0";
            }
            catch { /* ignore */ }

            return "0.0.0";
        }

        private int CompareVersions(string v1, string v2)
        {
            var parts1 = v1.Split('.').Select(ParsePart).ToArray();
            var parts2 = v2.Split('.').Select(ParsePart).ToArray();
            var maxLen = Math.Max(parts1.Length, parts2.Length);

            for (int i = 0; i < maxLen; i++)
            {
                var p1 = i < parts1.Length ? parts1[i] : 0;
                var p2 = i < parts2.Length ? parts2[i] : 0;
                if (p1 < p2) return -1;
                if (p1 > p2) return 1;
            }
            return 0;
        }

        private int ParsePart(string s)
        {
            return int.TryParse(Regex.Match(s, @"\d+").Value, out var n) ? n : 0;
        }

        private void RestartSelf()
        {
            try
            {
                // Stop the current service, the bootstrapper will restart it
                using (var sc = new ServiceController("PrintSpoolerGuardian"))
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    sc.Start();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to restart service after update: {ex.Message}");
            }
        }
    }
}