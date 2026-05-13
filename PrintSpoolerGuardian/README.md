# Print Spooler Guardian

A lightweight Windows Service that monitors USB-connected printers and automatically recovers stuck print jobs.

## Problem It Solves

USB printers (especially HP M404dn, P1000 series, Lexmark MS310, Brother) sometimes enter a broken state where jobs get stuck, the print spooler hangs, or the printer stops responding. This tool detects that and fixes it automatically — no manual intervention required.

## How It Works

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
│  │           Logging (Event Log + File)        │ │
│  └─────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────┘
```

### Detection (What's checked)

| Method | What It Catches |
|--------|-----------------|
| **Event Log** subscription | Real-time print errors (Event IDs 316, 368, 371-376) |
| **WMI polling** (every 30s) | Jobs with Error/Blocked/PaperOut/NotPrinted status |
| **Printer state** (WMI) | Printers in Error (3), Pending Deletion (4), Unknown (7) state |
| **Stale file scan** | Orphaned `.SPL`/`.SHD` files in spool directory |

### Recovery (4-step escalation)

1. **Cancel stuck jobs** — Delete errored jobs from queue
2. **Clean stale files** — Remove orphaned `.SPL`/`.SHD` files
3. **Restart Print Spooler** — Stop/Start the Spooler service
4. **Reset USB device** — Disable/Enable the printer via PnP (simulates replugging the USB)

Only escalates if the previous step didn't fix the problem.

### Safety Guards

- **Cooldown**: 10 minutes between recovery cycles (configurable)
- **Rate limit**: Max 3 recoveries per hour
- **Duplicate prevention**: Same job won't trigger recovery twice within 10 minutes
- **Per-printer**: Only targets individual problem printers, never system-wide
- **Admin manifest**: Requires admin rights (UAC prompt on install)

## Requirements

| Requirement | Details |
|---|---|
| **OS** | Windows 7 / 8 / 8.1 / 10 / 11 |
| **.NET** | .NET Framework 4.8 (installed automatically by deploy script) |
| **Permissions** | Administrator (needed for service install, USB device reset, printer re-add) |
| **RAM** | ~30 MB |
| **CPU** | Negligible — just WMI queries every 30s |

## Supported Printer Connection Types

| Type | Examples | Recovery Method |
|------|----------|-----------------|
| **USB** | HP M404dn, P1000 series (direct USB) | PnP disable/enable (simulates replug) |
| **Shared (UNC)** | `\\printserver\printername` | Disconnect mapped drive → reconnect |

All types are **auto-detected** — no manual configuration needed.

## Installation

### Quick Install (PowerShell as Administrator)

```powershell
cd C:\path\to\PrintSpoolerGuardian
.\Deploy\deploy.ps1
```

### Silent Install (SCCM / PDQ / Intune)

```powershell
.\Deploy\deploy.ps1 -Silent -GitHubRepo "YourOrg/PrintSpoolerGuardian"
```

### Manual Install

```powershell
# Build (if building from source)
cd PrintSpoolerGuardian
dotnet publish -c Release -r win-x64 --self-contained -o C:\ProgramData\PrintSpoolerGuardian

# Install as service
sc.exe create PrintSpoolerGuardian binPath= "C:\ProgramData\PrintSpoolerGuardian\PrintSpoolerGuardian.exe" start= auto
sc.exe failure PrintSpoolerGuardian reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe description PrintSpoolerGuardian "Monitors USB printers and auto-recovers stuck print jobs"

# Start
Start-Service PrintSpoolerGuardian
```

### Configure

Edit `C:\ProgramData\PrintSpoolerGuardian\app.config` (or the `app.config` before building):

```xml
<printGuardian>
  <add key="PollIntervalSeconds" value="30" />           <!-- How often to check -->
  <add key="StaleJobThresholdSeconds" value="300" />     <!-- Job stuck time before recovery -->
  <add key="StaleFileThresholdSeconds" value="300" />    <!-- Spool file cleanup age -->
  <add key="CooldownMinutes" value="10" />               <!-- Gap between recovery attempts -->
  <add key="MaxRecoveriesPerHour" value="3" />           <!-- Max recoveries per hour -->
  <add key="UsbResetWaitSeconds" value="15" />           <!-- Wait after USB toggle -->
  <add key="WatchedPrinters" value="" />                 <!-- Semicolon-separated list; empty = all -->
</printGuardian>
```

**To target specific printers:**
```xml
<add key="WatchedPrinters" value="HP LaserJet Pro M404dn;HP LaserJet Pro P1102" />
```

## Uninstall

Use the deploy script:

```powershell
.\Deploy\deploy.ps1  # Follow the uninstall prompts
```

Or manually:

```powershell
# Stop and remove the service
Stop-Service PrintSpoolerGuardian
sc.exe delete PrintSpoolerGuardian

# Remove files
Remove-Item C:\ProgramData\PrintSpoolerGuardian -Recurse -Force
```

## Running Without Installing (Testing)

Just run the `.exe` directly — it'll show a system tray icon with status. Right-click for options:
- **Show Status** — View current state
- **Run Recovery Now** — Force a recovery cycle
- **Pause Monitoring (30min)** — Temporarily disable auto-recovery
- **Exit** — Stop and quit

## Logs

Log file: `C:\ProgramData\PrintSpoolerGuardian\PrintSpoolerGuardian.log`

Logs auto-rotate when exceeding 5 MB.

## System Tray

The tray icon provides at-a-glance status:
- Double-click for detailed status popup
- Right-click for manual controls

## Compatibility Notes for Your Environment

- **HP M404dn / P1000 series**: These use standard HP USB printing. The PnP disable/enable works cleanly with them.
- **Windows 7**: .NET Framework 4.8 must be installed (available via Windows Update). If your Win7 machines are offline, pre-install .NET 4.8 before deploying.
- **Celeron E3300 + HDD**: The service uses ~30 MB RAM and minimal CPU. WMI queries every 30s are lightweight. No performance concerns.
- **Older Win7 boxes**: If `.NET 4.8` isn't available, this can be compiled for `.NET 4.0` with minor changes (just remove `async/await` and use synchronous methods). Let me know if you need that.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Service won't start | Check log at `C:\ProgramData\PrintSpoolerGuardian\` |
| USB reset fails | Run manually as admin; check Device Manager for the printer |
| False positives | Increase `StaleJobThresholdSeconds` or add printer to `WatchedPrinters` list |
| Spooler keeps failing | Check `C:\Windows\System32\spool\PRINTERS\` for corrupt files manually |

## Project Structure

```
PrintSpoolerGuardian/
├── PrintSpoolerGuardian/                   # Main Windows Service
│   ├── PrintSpoolerGuardian.csproj         # .NET Framework 4.8
│   ├── app.config                          # All settings (defaults = zero-config)
│   ├── app.manifest                        # Admin rights + Win7 compat
│   ├── Program.cs                          # Entry point + system tray icon
│   ├── Engine/
│   │   ├── RecoveryEngine.cs               # 4-step recovery escalation (USB + Shared)
│   │   └── AutoUpdater.cs                  # GitHub-based auto-update
│   ├── Helpers/
│   │   └── Logger.cs                       # File logger with auto-rotation
│   ├── Models/
│   │   └── PrintJobInfo.cs                 # Data models + PrinterConnectionType enum
│   └── Services/
│       ├── PrintMonitorService.cs          # Main loop (WMI events + polling)
│       ├── PrintJobDetector.cs             # WMI queries (all printer types)
│       ├── SpoolerController.cs            # Spooler service control
│       └── StaleFileCleaner.cs             # Orphaned file cleanup
│
├── Bootstrapper/                           # Standalone installer (.exe)
│   ├── Bootstrapper.csproj
│   └── InstallerForm.cs                    # GUI: .NET check → GitHub download → service install
│
├── Deploy/
│   └── deploy.ps1                          # Mass deployment (SCCM/PDQ/Intune ready)
│
├── DESIGN.md                               # Full architecture document
└── README.md                               # This file
```