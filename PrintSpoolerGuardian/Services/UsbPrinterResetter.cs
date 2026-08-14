using System;
using System.Management;
using System.Threading;

namespace PrintSpoolerGuardian
{
    /// <summary>Resets USB printer PnP devices through WMI.</summary>
    public class UsbPrinterResetter
    {
        public bool Reset(string deviceInstanceId, int waitAfterSeconds)
        {
            if (string.IsNullOrEmpty(deviceInstanceId))
            {
                Logger.Error("Cannot reset USB printer — device instance ID is null/empty.");
                return false;
            }

            try
            {
                Logger.Info("Disabling USB printer device: " + deviceInstanceId);
                using (var device = new ManagementObject(new ManagementPath(
                    "Win32_PnPEntity.DeviceID=\"" + EscapeInstanceId(deviceInstanceId) + "\"")))
                {
                    var inParams = device.GetMethodParameters("Disable");
                    var outParams = device.InvokeMethod("Disable", inParams, null);
                    uint disableResult = outParams == null ? 99 : Convert.ToUInt32(outParams["ReturnValue"]);
                    if (disableResult != 0)
                    {
                        Logger.Error("Failed to disable USB device " + deviceInstanceId + ". Return code: " + disableResult);
                        return false;
                    }

                    Logger.Info("USB device disabled successfully. Waiting " + waitAfterSeconds + "s before re-enabling...");
                    Thread.Sleep(waitAfterSeconds * 1000);
                    Logger.Info("Re-enabling USB printer device: " + deviceInstanceId);
                    var enableOut = device.InvokeMethod("Enable", null, null);
                    uint enableResult = enableOut == null ? 99 : Convert.ToUInt32(enableOut["ReturnValue"]);
                    if (enableResult != 0)
                    {
                        Logger.Error("Failed to enable USB device " + deviceInstanceId + ". Return code: " + enableResult);
                        return false;
                    }

                    Logger.Info("USB device re-enabled successfully. Waiting " + waitAfterSeconds + "s for printer to stabilize...");
                    Thread.Sleep(waitAfterSeconds * 1000);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("USB printer reset failed for " + deviceInstanceId + ": " + ex.Message);
                return false;
            }
        }

        public void ResetAllUsbPrinters(int waitAfterSeconds)
        {
            var deviceIds = new PrintJobDetector().GetAllUsbPrinterDeviceIds();
            foreach (var id in deviceIds)
            {
                Logger.Info("Resetting USB printer: " + id);
                Reset(id, waitAfterSeconds);
                Thread.Sleep(3000);
            }
        }

        private static string EscapeInstanceId(string id)
        {
            return id.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
