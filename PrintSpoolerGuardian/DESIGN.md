# Print Spooler Guardian — Design Document

## 1. Problem Statement

USB-connected printers intermittently fail — jobs get stuck in the queue, the spooler service hangs, or the printer itself becomes unresponsive. Network printers are unaffected. The current manual fix is: restart the print spooler service and/or physically power-cycle / reset the USB printer. This tool automates detection and recovery.

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────┐
│              Print Spooler Guardian              │
│                                                   │
│  ┌──────────┐    ┌───────────┐    ┌───────────┐ │
│  │ Monitor  │───▶│ Decision  │───▶│ Recovery  │ │
│  │  Loop    │    │  Engine   │    │ Engine    │ │
│  └──────────┘    └───────────┘    └───────────┘ │
│       │               │               │          │
│       ▼               ▼               ▼          │
│  ┌──────────┐    ┌───────────┐    ┌───────────┐ │
│  │ WMI      │    │ Config    │    │ Service   │ │
│  │ Events   │    │ Thresholds│    │ Controller│ │
│  │ Queue    │    │ Cooldowns │    │ USB Reset │ │
│  │ Polling  │    │ Escalation│    │           │ │
│  └──────────┘    └───────────┘    └───────────┘ │
│                                                   │
│  ┌─────────────────────────────────────────────┐ │
│  │           Logging & Alerts (optional)       │ │
│  └─────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────┘
```

**Runs as a Windows Service** (or optionally as a background process with a system tray icon for testing).

---

## 3. Detection Strategy (How it knows something is broken)

### Layer 1 — Event Log Monitoring (real-time, low overhead)
- Subscribe to **Windows Event Log** channels:
  - `Microsoft-Windows-PrintService/Operational` (Event IDs: 316, 368, 371, 372, 373, 374, 375, 376)
  - `System` log for spooler service errors (Event ID 7034 = service crashed, 7031 = service terminated unexpectedly)
- This gives us **instant notification** when something goes wrong without polling.

### Layer 2 — Periodic Queue Polling (backup, configurable interval)
- Query **WMI `Win32_PrintJob`** for jobs in error states:
  - `Status` contains "Error", "Blocked", "Paper Out", "Door Open", etc.
  - `JobStatus` flags: `ERROR` (0x00000002), `PAPEROUT` (0x00000010), `NOT_PRINTED` (0x00000020)
- Query **WMI `Win32_Printer`** for printer status:
  - `PrinterStatus` = 3 (Error), 4 (Pending Deletion), 7 (Unknown)
  - `DetectedErrorState` > 0 on USB printers
- Check the **spool directory** (`C:\Windows\System32\spool\PRINTERS\`) for stale `.SPL` / `.SHD` files older than configurable threshold (e.g., 5 minutes). Stale files = dead job.

### Detection Rules (configurable per-printer):
```yaml
detection:
  poll_interval_seconds: 30
  stale_job_threshold_seconds: 300      # job stuck > 5 min
  stale_file_threshold_seconds: 300     # SPL/SHD file older than 5 min
  error_event_count_trigger: 2          # N errors within window
  error_event_window_seconds: 60
  ignore_states: ["Printed", "Printed", "Deleted"]  # don't flag these
```

---

## 4. Recovery Engine (What it does when something is broken)

Recovery is **escalating** — start gentle, get aggressive:

### Step 1: Cancel Stuck Jobs
- Delete all jobs in error state from the queue via `Win32_PrintJob.Delete()`
- Clear stale files from spool directory
- Wait 5 seconds

### Step 2: Restart Print Spooler Service
```powershell
Restart-Service -Name Spooler -Force
```
- Or via SCM API: `ControlService(STOP)` → wait → `StartService()`
- Timeout: 30 seconds for stop, 30 seconds for start
- After restart, re-check printer status

### Step 3: Reset USB Printer Device (if Step 2 didn't help)
- **Method A (preferred):** Disable/Enable the USB PnP device:
  ```powershell
  Disable-PnpDevice -InstanceId "<device_instance_id>" -Confirm:$false
  Start-Sleep -Seconds 5
  Enable-PnpDevice -InstanceId "<device_instance_id>" -Confirm:$false
  ```
- **Method B (fallback):** Use `devcon.exe` (Microsoft-signed CLI tool, redistributable):
  ```
  devcon disable "<hardware_id>"
  devcon enable "<hardware_id>"
  ```
- **Method C (last resort):** Programmatically eject and re-enumerate the USB device using `CM_Request_Device_Eject` + Setup API. This simulates unplugging and replugging the USB cable.
- After reset, wait for printer to re-enumerate (check device arrival events or poll `Win32_Printer` for the printer coming back online).

### Step 4: Full Restart (if all else fails)
- Restart the PC (or optionally just the spooler + USB reset again with longer waits)
- Configurable whether to do this automatically or just alert

```yaml
recovery:
  escalation:
    - action: cancel_stuck_jobs
      wait_after_seconds: 5
    - action: restart_spooler
      timeout_seconds: 60
      wait_after_seconds: 10
    - action: reset_usb_device
      wait_after_seconds: 15
    - action: restart_spooler    # one more pass after USB reset
      timeout_seconds: 60
    - action: alert_admin        # if still broken, notify
    # - action: reboot            # optional nuclear option
  cooldown_between_recoveries_minutes: 10
  max_recoveries_per_hour: 3     # prevent infinite loops
```

---

## 5. USB Printer Identification

This only targets USB printers. Identify them by:
- `Win32_Printer.PortName` starts with "USB" or matches `USB\VID_XXXX&PID_XXXX\...`
- OR user config: explicit printer name allowlist
- Store the PnP device instance ID so we can do targeted USB reset without affecting other USB devices

---

## 6. Tech Stack

| Choice | Rationale |
|--------|-----------|
| **C# / .NET 8** | Native Windows API access, can install as Windows Service (`sc.exe` or `Microsoft.Extensions.Hosting.WindowsServices`), excellent WMI support, no external dependencies |
| **WMI (`System.Management`)** | Query print jobs, printer status, USB device info — all built into .NET |
| **Windows Service** | Runs in background, auto-starts with Windows, survives user logout |
| **Optional: .NET Worker Service** | Same thing but easier to develop/test; can run as console app during dev and install as service for prod |
| **Serilog** (or just built-in Event Log) | Structured logging to file + Windows Event Log |
| **appsettings.json** | Configuration — thresholds, which printers to watch, recovery steps to enable |

**Why not Python?** It works, but running as a Windows Service is clunkier, WMI bindings are less clean, and for a production IT admin tool on Windows, C# is the natural fit. Python is a fallback if you strongly prefer it.

**Why not PowerShell script?** PS is fine for one-off fixes but not suited for a long-running monitoring service with state tracking, cooldowns, and escalation logic.

---

## 7. Configuration File

```json
{
  "monitoring": {
    "pollIntervalSeconds": 30,
    "staleJobThresholdSeconds": 300,
    "staleFileThresholdSeconds": 300
  },
  "printers": [
    {
      "name": "HP LaserJet Pro (USB)",
      "usb": true,
      "portPattern": "USB",
      "enabled": true
    }
  ],
  "recovery": {
    "enabled": true,
    "escalationSteps": [
      "cancelStuckJobs",
      "restartSpooler",
      "resetUsbDevice",
      "restartSpooler"
    ],
    "cooldownMinutes": 10,
    "maxRecoveriesPerHour": 3,
    "spoolerTimeoutSeconds": 60,
    "usbResetWaitSeconds": 15
  },
  "logging": {
    "logToFile": true,
    "logPath": "C:\\Logs\\PrintGuardian",
    "logToEventLog": true,
    "minimumLevel": "Information"
  }
}
```

---

## 8. Safety & False-Positive Prevention

| Concern | Mitigation |
|---------|------------|
| Restarting spooler kills in-progress good jobs | Only trigger on printers with stuck/error jobs, not system-wide. Cancel only errored jobs first. |
| USB reset disrupts other devices on same hub | Target by specific device instance ID, not hub |
| Recovery loop (keeps restarting) | Cooldown timer + max recoveries per hour |
| False positive from momentary glitch | Require error state to persist across 2+ poll cycles (or match an event log error) |
| Service crashes | Windows Service recovery actions (restart on failure, up to 3 attempts) |

---

## 9. Monitoring / Observability

- **Windows Event Log** entries under `Application` log, source `PrintSpoolerGuardian` for every detection, recovery action, and outcome
- **Optional HTTP health endpoint** (if we add Kestrel) for monitoring dashboards
- **Status file**: `C:\ProgramData\PrintSpoolerGuardian\status.json` — last check time, last recovery, printer states

---

## 10. Deployment

```powershell
# Build
dotnet publish -c Release -r win-x64 --self-contained

# Install as Windows Service
sc.exe create PrintSpoolerGuardian binPath= "C:\Services\PrintSpoolerGuardian\PrintSpoolerGuardian.exe" start= auto
sc.exe description PrintSpoolerGuardian "Monitors USB printers and auto-recovers stuck print jobs"
sc.exe start PrintSpoolerGuardian
```

---

All namespaces flattened to `PrintSpoolerGuardian` for simplicity and clean referencing.

**Final Project Structure:**

```
PrintSpoolerGuardian/
├── PrintSpoolerGuardian/
│   ├── PrintSpoolerGuardian.csproj     # .NET Framework 4.8
│   ├── app.config                      # All settings (zero-config defaults)
│   ├── app.manifest                    # Admin privileges + Win7+ compat
│   ├── Program.cs                      # Entry point + system tray icon
│   ├── Engine/
│   │   ├── RecoveryEngine.cs           # 4-step recovery (USB + Shared)
│   │   └── AutoUpdater.cs              # GitHub-based auto-update
│   ├── Helpers/
│   │   └── Logger.cs                   # File logger with auto-rotation
│   ├── Models/
│   │   └── PrintJobInfo.cs             # Data models + PrinterConnectionType enum
│   └── Services/
│       ├── PrintMonitorService.cs      # Main loop (WMI events + polling)
│       ├── PrintJobDetector.cs         # WMI queries (all printer types)
│       ├── SpoolerController.cs        # Spooler service control
│       ├── UsbPrinterResetter.cs       # USB PnP disable/enable
│       └── StaleFileCleaner.cs         # Orphaned SPL/SHD cleanup
├── Bootstrapper/
│   ├── Bootstrapper.csproj
│   └── InstallerForm.cs                # GUI installer: .NET check + GitHub download + service
├── Deploy/
│   └── deploy.ps1                      # Mass deployment (SCCM/PDQ/Intune)
├── DESIGN.md
└── README.md

---

## Open Questions for You

1. **Should this also clear stale files from the spool directory**, or just handle the service/printer reset?
2. **How aggressive do you want the USB reset?** Disable/Enable PnP device is clean; full eject/re-enumerate is more thorough but riskier.
3. **Should there be a notification system** (email, toast, Event Log only)?
4. **Any specific printer models** you're dealing with? Some have quirks with USB reset.
5. **Do you want a companion GUI/system tray app** for manual controls and status viewing, or purely headless service?

---

*This is the blueprint. Once you give it a thumbs-up (or tell me what to change), I'll start building.*