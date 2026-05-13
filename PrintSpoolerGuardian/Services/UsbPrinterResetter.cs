using System;
using System.Management;
using System.Threading.Tasks;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Resets a USB printer by disabling and re-enabling its PnP device.
    /// No external tools required — uses WMI directly.
    /// </summary>
    public class UsbPrinterResetter
    {
        /// <summary>
        /// Resets a USB printer by its PnP device instance ID.
        /// Disables the device, waits, then re-enables it.
        /// Returns true if successful.
        /// </summary>
        public async Task<bool> ResetAsync(string deviceInstanceId, int waitAfterSeconds = 15)
        {
            if (string.IsNullOrEmpty(deviceInstanceId))
            {
                Logger.Error("Cannot reset USB printer — device instance ID is null/empty.");
                return false;
            }

            try
            {
                Logger.Info($"Disabling USB printer device: {deviceInstanceId}");

                using (var device = new ManagementObject(
                    new ManagementPath($"Win32_PnPEntity.DeviceID=\"{EscapeInstanceId(deviceInstanceId)}\"")))
                {
                    // Disable the device
                    var inParams = device.GetMethodParameters("Disable");
                    var outParams = device.InvokeMethod("Disable", inParams, null);
                    uint disableResult = outParams != null ? Convert.ToUInt32(outParams["ReturnValue"]) : 99;

                    if (disableResult != 0)
                    {
                        Logger.Error($"Failed to disable USB device {deviceInstanceId}. Return code: {disableResult}");
                        return false;
                    }

                    Logger.Info($"USB device disabled successfully. Waiting {waitAfterSeconds}s before re-enabling...");
                    await Task.Delay(waitAfterSeconds * 1000);

                    // Re-enable the device
                    Logger.Info($"Re-enabling USB printer device: {deviceInstanceId}");
                    var enableOut = device.InvokeMethod("Enable", null, null);
                    uint enableResult = enableOut != null ? Convert.ToUInt32(enableOut["ReturnValue"]) : 99;

                    if (enableResult != 0)
                    {
                        Logger.Error($"Failed to enable USB device {deviceInstanceId}. Return code: {enableResult}");
                        return false;
                    }

                    Logger.Info($"USB device re-enabled successfully. Waiting {waitAfterSeconds}s for printer to stabilize...");
                    await Task.Delay(waitAfterSeconds * 1000);

                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"USB printer reset failed for {deviceInstanceId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Resets ALL USB printers. Use with caution.
        /// </summary>
        public async Task ResetAllUsbPrintersAsync(int waitAfterSeconds = 15)
        {
            var detector = new PrintJobDetector();
            var deviceIds = detector.GetAllUsbPrinterDeviceIds();

            foreach (var id in deviceIds)
            {
                Logger.Info($"Resetting USB printer: {id}");
                await ResetAsync(id, waitAfterSeconds);
                await Task.Delay(3000);
            }
        }

        /// <summary>
        /// Escapes backslashes and quotes in a WMI device instance ID for use in a WQL query.
        /// </summary>
        private static string EscapeInstanceId(string id)
        {
            return id.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}