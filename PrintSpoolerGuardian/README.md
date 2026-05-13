# Print Spooler Guardian

A lightweight Windows app that monitors USB and shared printers and automatically recovers stuck print jobs.

## Problem It Solves

USB printers (especially HP M404dn, P1000 series) sometimes enter a broken state where jobs get stuck, the print spooler hangs, or the printer stops responding. This tool detects that and fixes it automatically — no manual intervention required.

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
│  └─────────────────────────────────────────────┘ │
│           File Logging + Tray Icon                │
└─────────────────────────────────────────────────┘
```

### Detection (What's checked)

| Method | What It Catches |
|--------|-----------------|
| **WMI events** (realtime) | Print job state changes |
| **WMI polling** (every 30s) | Jobs with Error/Blocked/PaperOut/NotPrinted status |
| **Printer state** (WMI) | Printers in Error (3), Pending Deletion (4), Unknown (7) |
| **Stale file scan** | Orphaned `.SPL`/`.SHD` files in spool directory |

### Recovery (4-step escalation)

1. **Cancel stuck jobs** — Delete errored jobs from queue
2. **Clean stale files** — Remove orphaned `.SPL`/`.SHD` files
3. **Restart Print Spooler** — Stop/Start the Spooler service
4. **Reset USB device** — PnP Disable/Enable (simulates replugging the USB)

For **shared printers** (UNC), step 4 is replaced with disconnecting and reconnecting the network path via `net use`.

Only escalates if the previous step didn't fix the problem.

### Safety Guards

- **Cooldown**: 10 minutes between recovery cycles (configurable)
- **Rate limit**: Max 3 recoveries per hour
- **Duplicate prevention**: Same job won't trigger recovery twice within 10 minutes
- **Per-printer targeting**: Only affects the stuck printer, never system-wide
- **Admin required**: UAC prompt — the app needs admin rights to restart the spooler and reset USB devices

## Requirements

| Requirement | Details |
|---|---|
| **OS** | Windows 7 / 8 / 8.1 / 10 / 11 |
| **.NET** | Self-contained build includes .NET 6.0 runtime (nothing to install) |
| **Permissions** | Administrator (for spooler restart, USB device reset, printer re-add) |
| **RAM** | ~30 MB |
| **CPU** | Negligible — just WMI queries every 30s |

## Supported Printer Types

| Type | Examples | Recovery Method |
|------|----------|-----------------|
| **USB** | Any printer connected via USB | PnP disable/enable (simulates replug) |
| **Shared (UNC)** | `\\printserver\printername` | `net use` disconnect → reconnect |
| **Network / TCP-IP** | Printers on raw IP ports | Not targeted (don't have this problem) |

All types are **auto-detected** — no manual configuration needed.

---

## Installation

### Quick Start — Download & Run

1. Download the ZIP from [Releases](https://github.com/BobanAliBrz/PrinterResetAliBrz/releases)
2. Extract it anywhere
3. Right-click `PrintSpoolerGuardian.exe` → **Run as Administrator**

A printer icon appears in the system tray. It auto-registers to start for all users on next login.

### Mass Deployment (SCCM / PDQ / Intune)

```powershell
.\Deploy\deploy.ps1 -Silent -GitHubRepo "BobanAliBrz/PrinterResetAliBrz"
```

### Build from Source

```powershell
dotnet publish -c Release -r win-x64 --self-contained -o .\publish
```

### Auto-Start

The app registers itself in the **All Users Startup folder** (`CommonStartup`) when first run as Administrator — no service setup needed. Every user on the machine gets it at login.

---

## Configuration

Edit the `app.config` next to the executable:

```xml
<printGuardian>
  <add key="PollIntervalSeconds" value="30" />           <!-- How often to check -->
  <add key="StaleJobThresholdSeconds" value="300" />     <!-- Job stuck time before recovery -->
  <add key="StaleFileThresholdSeconds" value="300" />    <!-- Spool file cleanup age -->
  <add key="CooldownMinutes" value="10" />               <!-- Gap between recovery attempts -->
  <add key="MaxRecoveriesPerHour" value="3" />           <!-- Max recoveries per hour -->
  <add key="UsbResetWaitSeconds" value="15" />           <!-- Wait after USB toggle -->
  <add key="UpdateGitHubRepo" value="BobanAliBrz/PrinterResetAliBrz" />
  <add key="WatchedPrinters" value="" />                 <!-- Semicolon-separated; empty = all -->
</printGuardian>
```

**To target specific printers only:**
```xml
<add key="WatchedPrinters" value="HP LaserJet Pro M404dn;HP LaserJet Pro P1102" />
```

---

## Uninstall

1. Right-click tray icon → **Exit**
2. Delete `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\Print Spooler Guardian.lnk`
3. Delete `C:\ProgramData\PrintSpoolerGuardian\`

Or use the bootstrapper installer's **Uninstall** button.

---

## System Tray

The printer icon in the system tray provides:

| Action | What it does |
|---|---|
| Double-click | Show status popup |
| Right-click → Show Status | Current state, last check, recoveries this hour |
| Right-click → Run Recovery Now | Force a full recovery cycle |
| Right-click → Pause Monitoring (30min) | Silence for 30 minutes |
| Right-click → Exit | Stop the app |

---

## Logs

Log file: `C:\ProgramData\PrintSpoolerGuardian\PrintSpoolerGuardian.log`

Auto-rotates when exceeding 5 MB.

---

## Compatibility Notes (Your Environment)

- **HP M404dn / P1000 series**: These use standard HP USB printing. The PnP disable/enable works cleanly.
- **Windows 7**: Self-contained .NET 6.0 build works on Win7 with no extra runtime install.
- **Celeron E3300 + HDD**: Uses ~30 MB RAM, minimal CPU. WMI queries every 30s are lightweight.
- **Auto-updates**: Checks GitHub every 24h. Just upload a new release and all PCs update themselves.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| App won't start | Run as Administrator (right-click → Run as admin) |
| USB reset fails | Check Device Manager — is the printer showing up? |
| False positives | Increase `StaleJobThresholdSeconds` or set `WatchedPrinters` |
| Spooler keeps failing | Check `C:\Windows\System32\spool\PRINTERS\` for corrupt files manually |

---

## Project Structure

```
PrintSpoolerGuardian/
├── Program.cs                          # Entry point + tray icon
├── app.config                          # All settings (defaults = zero-config)
├── app.manifest                        # Admin rights + Win7-11 compat
├── Engine/
│   ├── RecoveryEngine.cs               # 4-step recovery escalation
│   └── AutoUpdater.cs                  # GitHub-based auto-update
├── Helpers/
│   ├── Logger.cs                       # File logger with auto-rotation
│   └── IconHelper.cs                   # Tray printer icon (drawn in code)
├── Models/
│   └── PrintJobInfo.cs                 # Data models + PrinterConnectionType enum
├── Services/
│   ├── PrintMonitorService.cs          # Main loop (WMI events + polling)
│   ├── PrintJobDetector.cs             # WMI queries (all printer types)
│   ├── SpoolerController.cs            # Spooler stop/start/restart
│   ├── UsbPrinterResetter.cs           # USB PnP disable/enable
│   └── StaleFileCleaner.cs             # Orphaned SPL/SHD cleanup
├── Bootstrapper/                       # GUI installer (.exe)
├── Deploy/                             # Mass deployment script
├── DESIGN.md                           # Full architecture document
└── README.md                           # This file
```
