using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Checks GitHub releases periodically and auto-updates the application.
    /// Designed to run alongside the monitor service with minimal overhead.
    /// Downloads the platform-specific ZIP (win-x64 or win-x86) automatically.
    /// </summary>
    public class AutoUpdater
    {
        private readonly string _updateRepo;
        private readonly int _checkIntervalHours;
        private readonly string _installDirectory;
        private readonly string _currentVersion;
        private readonly string _platformRid;

        private static readonly HttpClient _httpClient;
        private Timer _checkTimer;
        private DateTime _lastCheck = DateTime.MinValue;

        static AutoUpdater()
        {
            var handler = new HttpClientHandler();
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "PrintSpoolerGuardian-AutoUpdater");
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
        }

        public AutoUpdater()
        {
            _updateRepo = ConfigurationManager.AppSettings["UpdateGitHubRepo"] ?? "";
            _checkIntervalHours = int.TryParse(
                ConfigurationManager.AppSettings["UpdateCheckIntervalHours"] ?? "24", out var h) ? h : 24;

            // Use AppContext.BaseDirectory — works for self-contained and single-file publish
            // (Assembly.GetExecutingAssembly().Location returns empty for single-file)
            _installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            _currentVersion = GetCurrentVersion();

            // Detect current platform for downloading the correct architecture ZIP
            _platformRid = RuntimeInformation.ProcessArchitecture == Architecture.X86
                ? "win-x86"
                : "win-x64";
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

            Logger.Info($"Auto-update enabled. Repository: {_updateRepo}, " +
                $"Check interval: {_checkIntervalHours}h, Platform: {_platformRid}");
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

                var (downloadUrl, latestVersion) = await GetLatestReleaseInfoAsync();

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    LastCheckResult = "Could not retrieve latest release info";
                    Logger.Warn(LastCheckResult);
                    return;
                }

                if (string.IsNullOrEmpty(latestVersion))
                {
                    LastCheckResult = "Could not determine version from release";
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
                        using (var response = await _httpClient.GetAsync(downloadUrl))
                        {
                            response.EnsureSuccessStatusCode();
                            using (var fs = new FileStream(zipPath, FileMode.Create))
                            {
                                await response.Content.CopyToAsync(fs);
                            }
                        }

                        // Extract to install directory (overwrite existing files)
                        ExtractZipOverwrite(zipPath, _installDirectory);
                        File.Delete(zipPath);

                        // Restart the app to pick up new binary
                        Logger.Info("Update downloaded and extracted. Restarting...");
                        _checkTimer?.Dispose();
                        RestartSelf();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Auto-update failed: {ex.Message}");
                        LastCheckResult = $"Update failed: {ex.Message}";
                        try { File.Delete(zipPath); } catch { }
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

        /// <summary>
        /// Queries the GitHub Releases API and returns the best download URL + version.
        /// Prefers the platform-specific ZIP (e.g. win-x64) but falls back to any ZIP.
        /// </summary>
        private async Task<(string url, string version)> GetLatestReleaseInfoAsync()
        {
            try
            {
                var apiUrl = $"https://api.github.com/repos/{_updateRepo}/releases/latest";
                var json = await _httpClient.GetStringAsync(apiUrl);

                // Extract version from "tag_name": "v2.0.0" or "tag_name": "2.0.0"
                var versionMatch = Regex.Match(json, @"""tag_name""\s*:\s*""v?(\d+\.\d+(?:\.\d+(?:\.\d+)?)?)""");
                var version = versionMatch.Success ? versionMatch.Groups[1].Value : null;

                // Find all ZIP download URLs
                var urlMatches = Regex.Matches(json, @"""browser_download_url""\s*:\s*""([^""]*\.zip)""");

                if (urlMatches.Count == 0)
                    return (null, version);

                // Prefer platform-specific ZIP (e.g. containing "win-x64" or "win-x86")
                string bestUrl = null;
                string fallbackUrl = null;

                foreach (Match m in urlMatches)
                {
                    var url = m.Groups[1].Value;
                    if (url.IndexOf(_platformRid, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bestUrl = url;
                        break;
                    }
                    if (fallbackUrl == null)
                        fallbackUrl = url;
                }

                var downloadUrl = bestUrl ?? fallbackUrl;

                // If no version from tag_name, try to extract from URL
                if (string.IsNullOrEmpty(version) && downloadUrl != null)
                    version = ExtractVersionFromUrl(downloadUrl);

                return (downloadUrl, version);
            }
            catch (Exception ex)
            {
                Logger.Debug($"GitHub API query failed: {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>
        /// Extracts a ZIP, overwriting any existing files. The bool-overwrite overload of
        /// ZipFile.ExtractToDirectory is available in .NET 8, but we use manual extraction
        /// for more control (skip files that are locked, etc.).
        /// </summary>
        private static void ExtractZipOverwrite(string zipPath, string destDir)
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    // Skip directory entries (they have no name but end with '/')
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                    {
                        Directory.CreateDirectory(Path.Combine(destDir, entry.FullName.TrimEnd('/')));
                        continue;
                    }

                    try
                    {
                        var destPath = Path.Combine(destDir, entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        entry.ExtractToFile(destPath, true);
                    }
                    catch (Exception ex)
                    {
                        // File may be locked (e.g. the running exe) — skip it
                        Logger.Debug($"Could not overwrite {entry.FullName}: {ex.Message}");
                    }
                }
            }
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
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    return FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "0.0.0";
            }
            catch { /* ignore */ }

            try
            {
                var exePath = Path.Combine(_installDirectory, "PrintSpoolerGuardian.exe");
                if (File.Exists(exePath))
                    return FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "0.0.0";
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
                // Start a new instance (picks up the updated binary)
                var exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(_installDirectory, "PrintSpoolerGuardian.exe");

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                // Kill this instance
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to restart after update: {ex.Message}");
            }
        }
    }
}
