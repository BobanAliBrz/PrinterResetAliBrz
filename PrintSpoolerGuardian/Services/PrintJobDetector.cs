using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Queries WMI for print job state and printer status.
    /// Detects USB and Shared printers.
    /// </summary>
    public class PrintJobDetector
    {
        /// <summary>
        /// Returns all problem print jobs across all printers (or only watched printers if configured).
        /// </summary>
        public List<PrintJobInfo> GetProblematicJobs(string[] watchedPrinterNames = null)
        {
            var jobs = new List<PrintJobInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PrintJob"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            var job = ParsePrintJob(obj);

                            if (watchedPrinterNames != null && watchedPrinterNames.Length > 0)
                            {
                                if (!watchedPrinterNames.Any(p =>
                                    job.PrinterName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                                    continue;
                            }

                            if (job.IsProblematic)
                                jobs.Add(job);
                        }
                        catch { /* Skip malformed WMI entries */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WMI query for print jobs failed: {ex.Message}");
            }

            return jobs;
        }

        /// <summary>
        /// Returns ALL printers in an error/problem state — USB or Shared.
        /// </summary>
        public List<PrinterConfig> GetProblemPrinters()
        {
            var printers = new List<PrinterConfig>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PortName FROM Win32_Printer"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            var name = obj["Name"]?.ToString() ?? "";
                            var port = obj["PortName"]?.ToString() ?? "";
                            var status = (uint?)(obj["PrinterStatus"]) ?? 0;
                            var detectedError = (uint?)(obj["DetectedErrorState"]) ?? 0;

                            // Only flag printers that are actually in a problem state
                            if (status != 3 && status != 4 && status != 7 && detectedError == 0)
                                continue;

                            var connType = ClassifyConnection(port);

                            printers.Add(new PrinterConfig
                            {
                                Name = name,
                                PortName = port,
                                ConnectionType = connType
                            });
                        }
                        catch { /* Skip */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WMI query for printers failed: {ex.Message}");
            }

            return printers;
        }

        /// <summary>
        /// Gets all USB printer PnP device instance IDs.
        /// </summary>
        public List<string> GetAllUsbPrinterDeviceIds()
        {
            var ids = new List<string>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PortName, DeviceID FROM Win32_Printer WHERE PortName LIKE 'USB%'"))
                {
                    foreach (ManagementObject printer in searcher.Get())
                    {
                        try
                        {
                            var deviceId = printer["DeviceID"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(deviceId))
                                ids.Add(deviceId);
                        }
                        catch { /* Skip */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WMI query for USB printer device IDs failed: {ex.Message}");
            }

            return ids;
        }

        /// <summary>
        /// Gets the device instance ID for a USB printer by its port name.
        /// </summary>
        public string GetUsbDeviceInstanceId(string portName)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_Printer WHERE PortName = '" +
                    portName.Replace("'", "''") + "'"))
                {
                    foreach (ManagementObject printer in searcher.Get())
                    {
                        var deviceId = printer["DeviceID"]?.ToString() ?? "";

                        using (var pnpSearcher = new ManagementObjectSearcher(
                            "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '" +
                            deviceId.Replace("\\", "\\\\").Replace("'", "''") + "%'"))
                        {
                            foreach (ManagementObject pnp in pnpSearcher.Get())
                            {
                                return pnp["DeviceID"]?.ToString() ?? "";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not resolve device instance ID for {portName}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Returns all printers that are NOT in an error state (healthy printers).
        /// </summary>
        public List<PrinterConfig> GetHealthyPrinters()
        {
            var printers = new List<PrinterConfig>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PortName, PrinterStatus, DetectedErrorState FROM Win32_Printer"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            var status = (uint?)(obj["PrinterStatus"]) ?? 0;
                            var detectedError = (uint?)(obj["DetectedErrorState"]) ?? 0;

                            // Skip error-state printers
                            if (status == 3 || status == 4 || status == 7 || detectedError > 0)
                                continue;

                            var name = obj["Name"]?.ToString() ?? "";
                            var port = obj["PortName"]?.ToString() ?? "";

                            printers.Add(new PrinterConfig
                            {
                                Name = name,
                                PortName = port,
                                ConnectionType = ClassifyConnection(port)
                            });
                        }
                        catch { /* Skip */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WMI query for healthy printers failed: {ex.Message}");
            }

            return printers;
        }

        /// <summary>
        /// Classifies a printer port into a connection type.
        /// </summary>
        public static PrinterConnectionType ClassifyConnection(string portName)
        {
            if (string.IsNullOrEmpty(portName))
                return PrinterConnectionType.Shared;

            var port = portName.ToUpperInvariant();

            if (port.StartsWith("USB"))
                return PrinterConnectionType.Usb;
            if (port.StartsWith("LPT"))
                return PrinterConnectionType.Usb;

            // Anything else (UNC path, share name, etc.) = Shared
            return PrinterConnectionType.Shared;
        }

        private PrintJobInfo ParsePrintJob(ManagementObject obj)
        {
            uint jobStatus = 0;
            uint.TryParse(obj["JobStatus"]?.ToString() ?? "0", out jobStatus);

            string name = obj["Name"]?.ToString() ?? "";
            string[] parts = name.Split(',');
            string printerName = parts.Length > 0 ? parts[0] : name;
            string jobId = parts.Length > 1 ? parts[1] : "";

            return new PrintJobInfo
            {
                JobId = jobId,
                PrinterName = printerName,
                DocumentName = obj["Document"]?.ToString() ?? "",
                Status = obj["Status"]?.ToString() ?? "",
                JobStatus = jobStatus,
                Owner = obj["Owner"]?.ToString() ?? "",
                Size = Convert.ToInt64(obj["Size"]?.ToString() ?? "0"),
                TotalPages = int.Parse(obj["TotalPages"]?.ToString() ?? "0"),
                PagesPrinted = int.Parse(obj["PagesPrinted"]?.ToString() ?? "0")
            };
        }
    }
}