# 🖨 Print Spooler Guardian

> **⚠️ DISCLAIMER: Personal project for work use**
>
> This is a personal tool I built for my own IT admin job. It works for my environment (HP printers, old PCs, Windows 7-11). You're welcome to use it if it fits your needs, but this is **not a commercial product** — no guarantees, no support, no SLA. Fork it, tweak it, do whatever you want with it.

**Zero-config USB & shared printer monitoring and auto-recovery for Windows.**

Automatically detects when a USB or shared (UNC/network) printer gets stuck, and recovers it through escalating steps — from canceling stuck jobs to resetting the USB device.

### Quick Start

```powershell
# Deploy (run as Administrator on each PC)
.\PrintSpoolerGuardian\Deploy\deploy.ps1

# Silent mass deployment (SCCM/PDQ/Intune)
.\PrintSpoolerGuardian\Deploy\deploy.ps1 -Silent -GitHubRepo "BobanAliBrz/PrinterResetAliBrz"
```

### What It Does

| Detection | Recovery |
|-----------|----------|
| WMI event subscription (real-time) | 1. Cancel stuck print jobs |
| Periodic polling every 30s | 2. Clean orphaned spool files |
| Print queue error states | 3. Restart Print Spooler service |
| Spooler service health | 4. Restart USB or reconnect shared printer |

### Requirements

- **OS**: Windows 7 / 8 / 8.1 / 10 / 11
- **.NET**: Framework 4.8 (installed automatically by deploy script)
- **RAM**: ~30 MB | **CPU**: Negligible

### Auto-Updates

Auto-updates are configured to check **BobanAliBrz/PrinterResetAliBrz** releases every 24 hours. Upload a ZIP with the binaries as a GitHub Release and all deployed PCs will auto-update.

### Project Structure

```
├── PrintSpoolerGuardian/         # Main Windows Service (.NET 4.8)
│   ├── app.config                # Zero-config defaults
│   ├── Program.cs                # Entry point + tray icon
│   ├── Engine/
│   │   ├── RecoveryEngine.cs     # USB + Shared printer recovery
│   │   └── AutoUpdater.cs        # GitHub auto-update
│   └── Services/
│       ├── PrintMonitorService.cs # Main loop (WMI + polling)
│       ├── PrintJobDetector.cs    # WMI queries
│       ├── SpoolerController.cs   # Service control
│       └── StaleFileCleaner.cs    # Spool file cleanup
├── Bootstrapper/                  # GUI installer (.exe)
├── Deploy/                        # Mass deployment script
└── README.md                      # This file
```

### Documentation

Full architecture docs: [`PrintSpoolerGuardian/DESIGN.md`](./PrintSpoolerGuardian/DESIGN.md)
Complete README: [`PrintSpoolerGuardian/README.md`](./PrintSpoolerGuardian/README.md)
