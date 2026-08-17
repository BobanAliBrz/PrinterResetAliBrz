using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;

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
                // 1. Query Win32_PnPEntity directly for USBPRINT devices
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Caption FROM Win32_PnPEntity WHERE Service = 'usbprint' OR DeviceID LIKE 'USBPRINT%'"))
                {
                    foreach (ManagementObject entity in searcher.Get())
                    {
                        try
                        {
                            var deviceId = entity["DeviceID"] != null ? entity["DeviceID"].ToString() : "";
                            if (!string.IsNullOrEmpty(deviceId) && !ids.Contains(deviceId))
                                ids.Add(deviceId);
                        }
                        catch { /* Skip */ }
                    }
                }

                // 2. Check Registry Enum\USBPRINT for any known device instances
                try
                {
                    using (var usbPrintKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBPRINT"))
                    {
                        if (usbPrintKey != null)
                        {
                            foreach (var modelKeyName in usbPrintKey.GetSubKeyNames())
                            {
                                using (var modelKey = usbPrintKey.OpenSubKey(modelKeyName))
                                {
                                    if (modelKey == null) continue;
                                    foreach (var instanceName in modelKey.GetSubKeyNames())
                                    {
                                        string pnpId = @"USBPRINT\" + modelKeyName + @"\" + instanceName;
                                        if (!ids.Contains(pnpId))
                                            ids.Add(pnpId);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Logger.Error("WMI query for USB printer device IDs failed: " + ex.Message);
            }

            return ids;
        }

        /// <summary>
        /// Gets the device instance ID for a USB printer by its port name and/or printer name.
        /// </summary>
        public string GetUsbDeviceInstanceId(string portName, string printerName = null)
        {
            try
            {
                // 1. Check registry USBPRINT records first for fastest and most reliable match
                string regMatch = ResolveUsbPnpIdFromRegistry(portName, printerName);
                if (!string.IsNullOrEmpty(regMatch))
                    return regMatch;

                // 2. Query Win32_PnPEntity for usbprint services or matching captions
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Caption, Name FROM Win32_PnPEntity WHERE Service = 'usbprint' OR DeviceID LIKE 'USBPRINT%'"))
                {
                    foreach (ManagementObject pnp in searcher.Get())
                    {
                        var devId = pnp["DeviceID"] != null ? pnp["DeviceID"].ToString() : "";
                        var caption = pnp["Caption"] != null ? pnp["Caption"].ToString() : "";
                        var name = pnp["Name"] != null ? pnp["Name"].ToString() : "";

                        if (!string.IsNullOrEmpty(printerName))
                        {
                            if (caption.IndexOf(printerName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                printerName.IndexOf(caption, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf(printerName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return devId;
                            }
                        }

                        if (!string.IsNullOrEmpty(portName) && devId.IndexOf(portName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return devId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("Could not resolve device instance ID for " + portName + "/" + printerName + ": " + ex.Message);
            }

            return null;
        }

        private static string ResolveUsbPnpIdFromRegistry(string portName, string printerName)
        {
            try
            {
                using (var usbPrintKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBPRINT"))
                {
                    if (usbPrintKey == null) return null;

                    foreach (var modelKeyName in usbPrintKey.GetSubKeyNames())
                    {
                        using (var modelKey = usbPrintKey.OpenSubKey(modelKeyName))
                        {
                            if (modelKey == null) continue;
                            foreach (var instanceName in modelKey.GetSubKeyNames())
                            {
                                using (var instKey = modelKey.OpenSubKey(instanceName))
                                {
                                    if (instKey == null) continue;
                                    var pName = instKey.GetValue("PortName") as string;
                                    var friendlyName = instKey.GetValue("FriendlyName") as string;
                                    var location = instKey.GetValue("LocationInformation") as string;

                                    bool portMatch = !string.IsNullOrEmpty(portName) &&
                                        (string.Equals(pName, portName, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(location, portName, StringComparison.OrdinalIgnoreCase) ||
                                         instanceName.IndexOf(portName, StringComparison.OrdinalIgnoreCase) >= 0);

                                    bool nameMatch = !string.IsNullOrEmpty(printerName) &&
                                        ((!string.IsNullOrEmpty(friendlyName) &&
                                          (friendlyName.IndexOf(printerName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           printerName.IndexOf(friendlyName, StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                         modelKeyName.IndexOf(printerName.Replace(" ", "_"), StringComparison.OrdinalIgnoreCase) >= 0);

                                    if (portMatch || nameMatch)
                                    {
                                        return @"USBPRINT\" + modelKeyName + @"\" + instanceName;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("Registry USBPRINT lookup exception: " + ex.Message);
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
