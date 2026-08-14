using System;
using System.Management;
using System.ServiceProcess;
using System.Threading;

namespace PrintSpoolerGuardian
{
    /// <summary>Controls the Print Spooler Windows service and its jobs.</summary>
    public class SpoolerController
    {
        public const string ServiceName = "Spooler";

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
                Logger.Error("Failed to get spooler status: " + ex.Message);
                return ServiceControllerStatus.Stopped;
            }
        }

        /// <summary>
        /// Restarts the spooler synchronously. Recovery already runs on its own
        /// worker thread, which keeps this compatible with .NET Framework 3.5.1.
        /// </summary>
        public bool Restart(int timeoutSeconds)
        {
            Logger.Info("Attempting to restart Print Spooler service...");
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    Logger.Info("Stopping Print Spooler...");
                    sc.Stop();
                    try
                    {
                        sc.WaitForStatus(ServiceControllerStatus.Stopped,
                            TimeSpan.FromSeconds(timeoutSeconds));
                    }
                    catch (System.TimeoutException)
                    {
                        Logger.Warn("Print Spooler did not stop within " + timeoutSeconds + "s, forcing...");
                        sc.Stop();
                        Thread.Sleep(2000);
                        sc.Refresh();
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            Logger.Error("Could not stop Print Spooler service.");
                            return false;
                        }
                    }

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

                    Logger.Error("Print Spooler service started but status is: " + sc.Status);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to restart Print Spooler: " + ex.Message);
                return false;
            }
        }

        public bool Stop(int timeoutSeconds)
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.StopPending)
                        return true;
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(timeoutSeconds));
                    return sc.Status == ServiceControllerStatus.Stopped;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to stop spooler: " + ex.Message);
                return false;
            }
        }

        public bool Start(int timeoutSeconds)
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending)
                        return true;
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(timeoutSeconds));
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start spooler: " + ex.Message);
                return false;
            }
        }

        public void CancelAllJobs(string printerName)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PrintJob"))
                {
                    foreach (ManagementObject job in searcher.Get())
                    {
                        try
                        {
                            var name = job["Name"] == null ? "" : job["Name"].ToString();
                            if (printerName == null || name.StartsWith(printerName, StringComparison.OrdinalIgnoreCase))
                            {
                                job.Delete();
                                Logger.Info("Cancelled print job: " + name);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to cancel print jobs: " + ex.Message);
            }
        }
    }
}
