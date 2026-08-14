# Print Spooler Guardian — Maintainer Memory

> **Repository:** `BobanAliBrz/PrinterResetAliBrz`
> **Current version:** `2.0.2.0`
> **Last reviewed:** 2026-08-14

## Purpose

Print Spooler Guardian is an administrator-run Windows tray application that detects and recovers stuck USB and shared/UNC printers. It is intended for older managed Windows PCs where restarting the spooler or reconnecting the printer is a frequent manual fix.

The application monitors print jobs and printer error states using WMI events plus periodic polling (30 seconds by default). Recovery is deliberately conservative: it cancels stuck jobs, cleans stale spool files, restarts the Print Spooler, then either resets a USB PnP device or refreshes a shared-printer connection.

## Current Architecture

- **App:** `PrintSpoolerGuardian/`, a WinForms tray app targeting `net8.0-windows`.
- **Deployment:** self-contained, partially trimmed builds for `win-x64` and `win-x86`. The installer also carries an app-local Universal CRT, so it does not require the VC++ runtime or KB2999226 on Windows 7.
- **Installer:** `PrintSpoolerGuardian/Installer/setup.iss` packages both architectures into one administrator-required Inno Setup executable.
- **Startup:** the installer creates an All Users Startup shortcut. The app is not a Windows service.
- **Updates:** `Engine/AutoUpdater.cs` checks GitHub Releases for a newer version and restarts the application after applying an update.

### Main code locations

| Path | Responsibility |
|---|---|
| `Program.cs` | Application entry point, tray UI, startup registration |
| `Services/PrintMonitorService.cs` | WMI event watcher, polling loop, job deduplication |
| `Services/PrintJobDetector.cs` | WMI queries and printer connection classification |
| `Engine/RecoveryEngine.cs` | Recovery orchestration, cooldown, and hourly rate limiting |
| `Services/SpoolerController.cs` | Print Spooler and job operations |
| `Services/UsbPrinterResetter.cs` | WMI PnP disable/enable for USB devices |
| `Services/StaleFileCleaner.cs` | Old `.SPL`/`.SHD` cleanup |
| `Engine/AutoUpdater.cs` | GitHub Release discovery, download, extraction, restart |
| `Installer/build.ps1` | Publishes x64/x86 and builds the installer |

## Operating Defaults

Configuration is in `PrintSpoolerGuardian/app.config`.

| Setting | Default | Meaning |
|---|---:|---|
| `PollIntervalSeconds` | 30 | Polling interval |
| `StaleJobThresholdSeconds` | 300 | Age before a job triggers recovery |
| `StaleFileThresholdSeconds` | 300 | Age before a spool file is cleaned |
| `CooldownMinutes` | 10 | Minimum interval between recovery cycles |
| `MaxRecoveriesPerHour` | 3 | Maximum recovery cycles per hour |
| `UpdateCheckIntervalHours` | 24 | Release-check frequency; `0` disables it |

Logs are written to `C:\ProgramData\PrintSpoolerGuardian` by default. `WatchedPrinters` is a semicolon-separated filter; an empty value means no name filter.

## Build and Release

The version must stay aligned in the project file and installer default:

- `PrintSpoolerGuardian/PrintSpoolerGuardian.csproj` (`Version`, `AssemblyVersion`, `FileVersion`)
- `PrintSpoolerGuardian/Installer/setup.iss` (`MyAppVersion` fallback)

Build the installer from the app directory:

```powershell
.\Installer\build.ps1
```

The output is `dist/PrintSpoolerGuardian_Setup_v<version>.exe`. For a release, create and push a matching Git tag and attach that installer to the GitHub Release. Use the release notes to state the user-visible changes and any compatibility impact.

The next release artifact is `dist/PrintSpoolerGuardian_Setup_v2.0.2.0.exe`.

## Compatibility and Constraints

- The installer declares Windows 7 SP1 and later, on 32-bit and 64-bit systems.
- Elevation is required to operate the spooler and reset PnP devices; users can see a UAC prompt at launch.
- WMI eventing is an acceleration path; polling must remain functional because WMI subscriptions can be unreliable.
- Do not reintroduce Windows Service installation while the app depends on a visible tray UI.
- Be cautious with trimming: `System.Management` and `System.Configuration.ConfigurationManager` are explicitly rooted because they use reflection.

## Maintenance Rules

- Update `changelog.md` for every user-visible, behavior-changing, or release-related change. Add unreleased work under `## [Unreleased]` before release; move it into a dated version section when publishing.
- Keep this file concise and current. Put detailed design rationale in `PrintSpoolerGuardian/DESIGN.md`, and release history in `changelog.md`.
- Do not commit `publish/`, `dist/`, or release archives; release artifacts belong on GitHub Releases.
