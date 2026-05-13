using System;
using System.Management;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Controls the Print Spooler Windows service — stop, start, restart, and status check.
    /// </summary>
    public class SpoolerController
    {
        public const string ServiceName = "Spooler";

        /// <summary>
        /// Gets the current state of the Print Spooler service.
        /// </summary>
        public ServiceControllerStatus GetStatus()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    sc.Refresh();
                    return sc.Status;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to get spooler status: {ex.Message}");
                return ServiceControllerStatus.Stopped;
            }
        }

        /// <summary>
        /// Restarts the Print Spooler service. Returns true if successful.
        /// </summary>
        public async Task<bool> RestartAsync(int timeoutSeconds = 60)
        {
            Logger.Info("Attempting to restart Print Spooler service...");

            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    // Stop
                    Logger.Info("Stopping Print Spooler...");
                    sc.Stop();

                    var stopCts = new CancellationTokenSource();
                    stopCts.CancelAfter(timeoutSeconds * 1000);

                    try
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped,
                            TimeSpan.FromSeconds(timeoutSeconds));
                    }
                    catch (TimeoutException)
                    {
                        Logger.Warn($"Print Spooler did not stop within {timeoutSeconds}s, forcing...");
                        sc.Stop();
                        await Task.Delay(2000);
                        sc.Refresh();
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            Logger.Error("Could not stop Print Spooler service.");
                            return false;
                        }
                    }

                    // Start
                    Logger.Info("Starting Print Spooler...");
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running,
                        TimeSpan.FromSeconds(timeoutSeconds));

                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        Logger.Info("Print Spooler service restarted successfully.");
                        return true;
                    }
                    else
                    {
                        Logger.Error($"Print Spooler service started but status is: {sc.Status}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to restart Print Spooler: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops the spooler only.
        /// </summary>
        public bool Stop(int timeoutSeconds = 30)
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped ||
                        sc.Status == ServiceControllerStatus.StopPending)
                        return true;

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped,
                        TimeSpan.FromSeconds(timeoutSeconds));
                    return sc.Status == ServiceControllerStatus.Stopped;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to stop spooler: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the spooler only.
        /// </summary>
        public bool Start(int timeoutSeconds = 30)
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running ||
                        sc.Status == ServiceControllerStatus.StartPending)
                        return true;

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running,
                        TimeSpan.FromSeconds(timeoutSeconds));
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start spooler: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clears all print jobs from the queue for a given printer.
        /// </summary>
        public void CancelAllJobs(string printerName = null)
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT * FROM Win32_PrintJob"))
                {
                    foreach (System.Management.ManagementObject job in searcher.Get())
                    {
                        try
                        {
                            var name = job["Name"]?.ToString() ?? "";
                            if (printerName == null ||
                                name.StartsWith(printerName, StringComparison.OrdinalIgnoreCase))
                            {
                                job.Delete();
                                Logger.Info($"Cancelled print job: {name}");
                            }
                        }
                        catch { /* Skip jobs we can't delete */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to cancel print jobs: {ex.Message}");
            }
        }
    }
}