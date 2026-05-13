# 🖨 Print Spooler Guardian

> **⚠️ DISCLAIMER: Personal project for work use**
>
> This is a personal tool I built for my own IT admin job. It works for my environment (HP printers, old PCs, Windows 7-11). You're welcome to use it if it fits your needs, but this is **not a commercial product** — no guarantees, no support, no SLA. Fork it, tweak it, do whatever you want with it.

**Zero-config USB & shared printer monitoring and auto-recovery for Windows.**

Automatically detects when a USB or shared (UNC) printer gets stuck, and recovers it through escalating steps — from canceling stuck jobs to resetting the USB device.

---

## Quick Start

### Option 1: Download & Run (easiest)

1. Go to **[Releases](https://github.com/BobanAliBrz/PrinterResetAliBrz/releases)** on GitHub
2. Download the latest `PrintSpoolerGuardian_vX.X.X.zip`
3. Extract it anywhere on the target PC
4. **Right-click** `PrintSpoolerGuardian.exe` → **Run as Administrator**

That's it. It starts monitoring immediately. A printer icon appears in the system tray. It also registers itself to auto-start for every user on that PC on next login.

### Option 2: Mass Deployment (SCCM / PDQ / Intune)

```powershell
# Run as Administrator on each target PC
.\PrintSpoolerGuardian\Deploy\deploy.ps1 -Silent -GitHubRepo "BobanAliBrz/PrinterResetAliBrz"
```

---

## Requirements

- **OS**: Windows 7 / 8 / 8.1 / 10 / 11
- **.NET**: Framework 4.8 (or .NET 6.0 runtime for the self-contained build)
- **RAM**: ~30 MB | **CPU**: Negligible

---

## What It Does

| Detection | Recovery |
|-----------|----------|
| WMI event subscription (real-time) | 1. Cancel stuck print jobs |
| Periodic polling every 30s | 2. Clean orphaned spool files |
| Print queue error states | 3. Restart Print Spooler service |
| Spooler service health | 4. Reset USB device or reconnect shared printer |

**Safety guards:** Max 3 recoveries per hour, 10-min cooldown between cycles, per-job deduplication. Manual pause from tray icon if you need to work on a printer.

---

## Auto-Updates

Once running, it checks **BobanAliBrz/PrinterResetAliBrz** releases every 24 hours. Upload a new ZIP as a GitHub Release and all deployed PCs will auto-update within a day.

---

## How to Use the Tray Icon

| Menu Item | What it does |
|---|---|
| Show Status | Current state, last check time, recoveries this hour |
| Run Recovery Now | Force a full recovery cycle immediately |
| Pause Monitoring (30min) | Silence for 30 minutes |
| Exit | Stop the app |

---

## Project Structure

```
├── PrintSpoolerGuardian/         # Main app (.NET 6.0-windows)
│   ├── app.config                # Zero-config defaults
│   ├── Program.cs                # Entry point + tray icon
│   ├── Engine/
│   │   ├── RecoveryEngine.cs     # USB + Shared printer recovery
│   │   └── AutoUpdater.cs        # GitHub auto-update
│   ├── Services/
│   │   ├── PrintMonitorService.cs # Main loop (WMI + polling)
│   │   ├── PrintJobDetector.cs    # WMI queries
│   │   ├── SpoolerController.cs   # Spooler control
│   │   ├── UsbPrinterResetter.cs  # USB PnP disable/enable
│   │   └── StaleFileCleaner.cs    # Spool file cleanup
│   └── Helpers/
│       ├── Logger.cs              # File logger
│       └── IconHelper.cs          # Tray icon
├── Bootstrapper/                  # GUI installer (.exe)
├── Deploy/                        # Mass deployment script
└── README.md
```

---

## Documentation

Full architecture docs: [`PrintSpoolerGuardian/DESIGN.md`](./PrintSpoolerGuardian/DESIGN.md)
Detailed project memory: [`project_memory.md`](./project_memory.md)
