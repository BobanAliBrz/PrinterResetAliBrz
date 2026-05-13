# Print Spooler Guardian — Project Memory

> **Last updated:** 2026-05-13
> **Author:** Sisyphus (Orchestration Agent)
> **Repository:** `BobanAliBrz/PrinterResetAliBrz` (GitHub)
> **Target audience:** Another AI agent or human maintainer

---

## 1. What Is This Project?

A **zero-config Windows service** that automatically detects when a USB or shared network printer gets stuck, and recovers it through escalating steps — from canceling stuck jobs all the way to resetting the USB PnP device.

**Why it exists:** An IT admin with ~100s of old PCs (Celeron E3300, HDDs, Windows 7) had constant printer failures. The manual fix was "restart spooler + replug USB", which had to be done dozens of times per day. This tool automates that entirely.

---

## 2. What It Does (High-Level)

| Trigger | Detection | Recovery |
|---|---|---|
| Print job hangs in queue | WMI event subscription (real-time) + 30s polling | Escalating: cancel jobs → clean spool files → restart spooler → reset USB device / reconnect shared printer |
| Printer shows error state | `Win32_Printer` status + `Win32_PrintJob` error flags | Same escalation |
| Stale `.SPL`/`.SHD` files in spool dir | File age check | Clean orphaned files |
| New GitHub release published | 24h periodic check via API | Auto-download ZIP, extract, restart service |

**Only USB and shared (UNC) printers are targeted.** Network/TCP-IP printers are excluded (they don't have this problem per the user).

---

## 3. Project Structure (Complete)

```
C:\Coding\PrinterAliBrz\
├── .gitignore                         # Standard .NET gitignore + publish/ + *.zip
├── README.md                          # Project README
├── project_memory.md                  # THIS FILE
│
└── PrintSpoolerGuardian\              # Main .NET 6.0-windows project (WinExe)
    ├── PrintSpoolerGuardian.csproj    # SDK-style project, targets net6.0-windows
    ├── Program.cs                     # Entry point + system tray icon (153 lines)
    ├── app.config                     # All configuration (zero-config defaults)
    ├── app.manifest                   # Admin elevation + Win7-11 compat
    │
    ├── Models/
    │   └── PrintJobInfo.cs            # PrinterConfig, PrintJobInfo, PrinterConnectionType enum
    │
    ├── Helpers/
    │   ├── Logger.cs                  # Thread-safe file logger with 5MB auto-rotation
    │   └── IconHelper.cs              # Programmatic printer icon (no .ico file needed)
    │
    ├── Engine/
    │   ├── RecoveryEngine.cs          # Escalation orchestrator (USB + Shared recovery)
    │   └── AutoUpdater.cs             # GitHub Releases checker + self-update
    │
    ├── Services/
    │   ├── PrintMonitorService.cs     # Main loop: WMI events + 30s polling + state tracking
    │   ├── PrintJobDetector.cs        # All WMI queries (jobs, printers, PnP devices)
    │   ├── SpoolerController.cs       # Service stop/start/restart + cancel jobs
    │   ├── UsbPrinterResetter.cs      # PnP Disable/Enable via WMI (no external tools)
    │   └── StaleFileCleaner.cs        # Orphaned SPL/SHD file cleanup
    │
    ├── Bootstrapper\                  # Separate .NET 4.8 WinForms project
    │   ├── Bootstrapper.csproj        # Targets net48 (can install .NET if missing)
    │   └── InstallerForm.cs           # GUI installer: checks .NET, downloads from GitHub, registers startup
    │
    ├── Deploy\
    │   └── deploy.ps1                 # PowerShell mass-deployment script (SCCM/PDQ/Intune)
    │
    ├── DESIGN.md                      # Original design document
    ├── README.md                      # Per-project readme
    └── publish\                       # Self-contained publish output (gitignored)
```

---

## 4. How It Works — Detailed Architecture

### 4.1 Detection Pipeline

**Two-layer detection** runs in `PrintMonitorService.RunAsync()`:

1. **WMI Event Subscription (real-time):** Subscribes to `__InstanceModificationEvent` on `Win32_PrintJob` WITHIN 10 seconds. When a job enters an error status, it fires immediately. This runs in a background thread (`WatchPrintEvents`).

2. **Polling Loop (every 30s):** On every tick, `CheckOnceAsync()` does:
   - Query `Win32_PrintJob` for problem jobs (error, blocked, paper out, etc.)
   - Query `Win32_Printer` for error-state printers (PrinterStatus=3/4/7 or DetectedErrorState>0)
   - Clean stale `.SPL`/`.SHD` files older than threshold (default 300s)
   - Merge and deduplicate printers that need recovery
   - Execute recovery on each unique printer

### 4.2 Printer Connection Classification

`PrintJobDetector.ClassifyConnection(portName)` determines the connection type:

- **Port starts with "USB"** → `PrinterConnectionType.Usb`
- **Port starts with "LPT"** → `PrinterConnectionType.Usb` (parallel = same physical reset behavior)
- **Everything else** (UNC path like `\\server\printer`, share names, etc.) → `PrinterConnectionType.Shared`

### 4.3 USB Recovery (4 escalating steps)

In `RecoveryEngine.ExecuteUsbRecoveryAsync()`:

| Step | Action | Details |
|---|---|---|
| 1 | Cancel stuck jobs | `SpoolerController.CancelAllJobs()` — deletes all WMI `Win32_PrintJob` entries for this printer |
| 2 | Clean stale spool files | `StaleFileCleaner.CleanStaleFiles()` — deletes orphaned `.SPL`/`.SHD` from `C:\Windows\System32\spool\PRINTERS\` |
| 3 | Restart Print Spooler | `SpoolerController.RestartAsync()` — stop service, wait for stopped, start service, wait for running (60s timeout each) |
| 4 | Reset USB PnP device | `UsbPrinterResetter.ResetAsync()` — uses WMI to call `Win32_PnPEntity.Disable()`, waits 15s, calls `Enable()`, then restarts spooler again |

After steps 3 and 4, `IsPrinterStillBroken()` checks if the printer recovered. If step 3 resolved it, step 4 is skipped.

### 4.4 Shared Printer Recovery (3 escalating steps)

In `RecoveryEngine.ExecuteSharedRecoveryAsync()`:

| Step | Action | Details |
|---|---|---|
| 1 | Cancel stuck jobs | Same as USB |
| 2 | Clean stale spool files | Same as USB |
| 3 | Disconnect and reconnect UNC mapping | Uses `net use \\server\printer /delete /y` then `net use \\server\printer /persistent:yes`. Also removes/re-adds via `rundll32 printui.dll,PrintUIEntry`. Then restarts spooler. |

### 4.5 Safety Guards (Built-in)

- **Cooldown:** 10 minutes between recovery cycles (configurable via `CooldownMinutes`)
- **Rate limit:** Max 3 recoveries per hour (configurable via `MaxRecoveriesPerHour`)
- **Job dedup:** Per-job tracking with 10-minute expiry (`_alertedJobs` dictionary) — prevents hammering the same stuck job
- **Cancellation-safe:** Uses `CancellationToken` across the entire pipeline, 500ms check intervals
- **Manual pause:** Tray icon pause for 30 minutes

### 4.6 Auto-Update System

In `AutoUpdater`:

1. On start, waits 5 minutes then checks GitHub Releases API
2. Every 24 hours thereafter (configurable)
3. Parses `browser_download_url` from GitHub API response using regex (no JSON library dependency)
4. Compares current version (from `FileVersionInfo.ProductVersion`) against latest tag
5. If newer: downloads ZIP to temp, extracts to install directory, launches a new instance of the app (with `runas` elevation), then exits the current process
6. Extraction has a fallback: if `ZipFile.ExtractToDirectory` fails, it iterates entries manually filtering for `.exe`, `.dll`, `.config`, `.manifest`, `.pdb` only

---

## 5. Key Files and Their Roles

| File | Role |
|---|---|
| `app.config` | All configuration lives here. No user-facing config UI — edit this file directly. Key settings: `PollIntervalSeconds`, `CooldownMinutes`, `MaxRecoveriesPerHour`, `WatchedPrinters` (blank=all), `UpdateGitHubRepo` |
| `app.manifest` | Mandates admin elevation (`requireAdministrator`). Declares Win7-11 compatibility. Enables modern Windows Common Controls. |
| `Program.cs` | Console-less WinForms app (`WinExe` output type). Creates `NotifyIcon` system tray with context menu. Runs `PrintMonitorService.RunAsync()` on background thread. |
| `PrintMonitorService.cs` | The brain. Owns the event watcher thread + polling loop. Tracks alerted jobs, deduplicates printers, delegates to `RecoveryEngine`. |
| `PrintJobDetector.cs` | Pure WMI queries. Gets problem jobs, problem printers, healthy printers, USB device IDs. Classifies connection type. |
| `RecoveryEngine.cs` | Orchestrates escalation. Routes USB vs Shared. Tracks cooldown + rate limit history. |
| `AutoUpdater.cs` | Timer-based GitHub checker. Self-extracting and self-restarting. |
| `Logger.cs` | File logger. Thread-safe via lock. Auto-rotates at 5MB. |
| `IconHelper.cs` | Draws a 16x16 printer icon with GDI+ (paper, body, LED dot). No external .ico file needed. |
| `UsbPrinterResetter.cs` | WMI Disable/Enable via `Win32_PnPEntity`. No `devcon.exe` dependency. |
| `InstallerForm.cs` | GUI installer for end-users. Checks/installs .NET 4.8, downloads from GitHub, registers in All Users Startup, creates desktop shortcut. |

---

## 6. Configuration Reference (`app.config`)

All values are in `<printGuardian>` section as key-value pairs:

```xml
<add key="PollIntervalSeconds" value="30" />         <!-- How often to poll printers (seconds) -->
<add key="StaleJobThresholdSeconds" value="300" />    <!-- Job stuck this long before triggering recovery -->
<add key="StaleFileThresholdSeconds" value="300" />   <!-- Spool file age before cleanup -->
<add key="CooldownMinutes" value="10" />              <!-- Min time between full recovery cycles -->
<add key="MaxRecoveriesPerHour" value="3" />          <!-- Cap on recovery attempts -->
<add key="SpoolerTimeoutSeconds" value="60" />        <!-- Timeout for spooler stop/start (each) -->
<add key="UsbResetWaitSeconds" value="15" />          <!-- Wait after USB disable/enable -->
<add key="StepWaitSeconds" value="5" />               <!-- Wait between escalation steps -->
<add key="LogDirectory" value="C:\ProgramData\PrintSpoolerGuardian" />
<add key="WatchedPrinters" value="" />                <!-- Semicolon-separated; blank=all printers -->
<add key="UpdateGitHubRepo" value="BobanAliBrz/PrinterResetAliBrz" />
<add key="UpdateCheckIntervalHours" value="24" />     <!-- 0 = disable auto-updates -->
```

---

## 7. Build & Deploy

### Build (Dev machine)

```
dotnet publish -c Release -r win-x64 --self-contained -o .\publish
```

Requires .NET 8 SDK (target framework is `net6.0-windows` for Win7 compat but SDK must be 6+).

### Deploy (to each PC)

```powershell
# Interactive (double-click or run in terminal)
.\PrintSpoolerGuardian\Deploy\deploy.ps1

# Silent (SCCM/PDQ/Intune)
.\PrintSpoolerGuardian\Deploy\deploy.ps1 -Silent -GitHubRepo "BobanAliBrz/PrinterResetAliBrz"
```

The deploy script:
1. Checks if .NET 4.8 is installed; if not, downloads and installs it
2. Downloads the latest release ZIP from GitHub (or uses local build if found)
3. Extracts to `C:\ProgramData\PrintSpoolerGuardian\`
4. Creates a shortcut in `CommonStartup` (All Users Startup folder) so the app launches for every user at logon
5. Cleans up any old Windows Service installation (migration)
6. Launches the app immediately
7. Creates desktop shortcut (interactive mode only)

### Publish a New Version

```powershell
# 1. Build
dotnet publish -c Release -r win-x64 --self-contained -o .\publish

# 2. Zip
Compress-Archive -Path .\publish\* -DestinationPath PrintSpoolerGuardian_vX.X.X.zip

# 3. Upload to GitHub Releases
# ZIP filename MUST contain version in format "vX.X.X" or "X.X.X" for auto-updater to work

---

## Auto-Start Mechanism

**Before v1.1:** Installed as a Windows Service via `sc.exe create` + `start= auto`.  
**Current:** Creates a `.lnk` shortcut in `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\` (All Users).

**Why the change:** The app is fundamentally a tray app with a GUI (`NotifyIcon`), not a proper Windows Service. SCM expected a service control handler (`ServiceBase.Run()`), which never came — causing duplicate instances and Event Log errors. The Startup folder approach is simpler, more transparent, and doesn't require `System.ServiceProcess`.

**How it registers:**
1. **On first admin run:** `Program.RegisterStartup()` checks if admin, creates shortcut in `CommonStartup`, logs it.
2. **Deploy script:** creates the shortcut directly.
3. **Bootstrapper installer:** creates the shortcut and launches the app.

**Restart after auto-update:** Instead of `ServiceController.Stop()` + `Start()`, the updater launches a new process with `Process.Start(Verb="runas")` and exits the current one.
```

---

## 8. Tricks and Lessons Learned

### 8.1 USB Reset Via WMI Instead of devcon.exe

**Problem:** Resetting a USB printer usually requires `devcon.exe` (a Microsoft tool that must be downloaded separately and distributed with the app). Adding an external dependency to 100s of PCs is a maintenance nightmare.

**Solution:** Used WMI's `Win32_PnPEntity` class to call `Disable()` and `Enable()` methods directly. This is built into Windows since Vista, requires no extra files, and works identically on Win7 through Win11.

```csharp
using (var device = new ManagementObject(
    new ManagementPath($"Win32_PnPEntity.DeviceID=\"{escaped}\"")))
{
    device.InvokeMethod("Disable", null, null);
    await Task.Delay(15000);
    device.InvokeMethod("Enable", null, null);
}
```

**Caveat:** The `DeviceID` must be escaped properly — backslashes become `\\\\` and quotes become `\\\"`.

### 8.2 Shared Printer Recovery Without Removing the Printer

**Problem:** Removing and re-adding a shared printer via Windows API requires printer driver knowledge, admin rights, and often breaks printer preferences (paper size, trays, etc.). For 100s of PCs with different drivers, this is fragile.

**Solution:** Instead of removing the printer entirely, use `net use` to disconnect/reconnect the UNC path (which re-establishes the SMB session), then use `printui.dll` to re-register it. This avoids driver reinstallation while clearing whatever session state was stuck.

```csharp
// Disconnect
RunHidden("net", $"use {uncPath} /delete /y");
// Reconnect
RunHidden("net", $"use {uncPath} /persistent:yes");
// Re-register in Printers folder
RunHidden("rundll32.exe", $"printui.dll,PrintUIEntry /ga /n \"{uncPath}\" /c \"{server}\"");
```

### 8.3 No Newtonsoft.Json Dependency

**Problem:** Adding a NuGet package for JSON parsing just to read the GitHub API response increases the publish size and surface area.

**Solution:** Used regex to parse the minimal JSON returned by GitHub's releases API. The response is well-known and stable, and we only need one field (`browser_download_url`). This kept the project dependency-free (beyond `System.Management` and `System.ServiceProcess.ServiceController`).

```csharp
var match = Regex.Match(json,
    @"""browser_download_url""\s*:\s*""([^""]*(?:\.zip|\.msi))""");
```

### 8.4 Thread-Safe File Logger With Auto-Rotation

**Problem:** A Windows service with concurrent threads writing to the same log file will corrupt it.

**Solution:** Used `lock` statement on a static object for thread safety, and check file size on every write — rotate at 5MB by renaming to timestamped backup.

### 8.5 Self-Contained Publish Without SDK on Target

**Problem:** Target PCs run Windows 7 with no .NET SDK and potentially no .NET runtime.

**Solution:** Published with `--self-contained` which bundles the .NET 6.0 runtime, native Windows dependencies, and all assemblies into a ~69MB folder. No runtime installation required on the target PC.

### 8.6 Bootstrapper Targets net48 (NOT net6.0)

**Problem:** The bootstrapper installer needs to run even on a bare Windows 7 PC that has NO .NET at all. If the bootstrapper targets .NET 6.0, you'd need .NET 6.0 to run the installer that installs .NET 4.8 — chicken and egg.

**Solution:** The bootstrapper project targets `net48` (.NET Framework 4.8). Windows 7 ships with .NET 3.5 SP1. If 4.8 isn't installed, the bootstrapper downloads and runs the 4.8 web installer, then proceeds. The main service app targets `net6.0-windows` and is self-contained, so it doesn't need any runtime.

### 8.7 WMI Event Subscription Limitations

**Problem:** WMI event subscriptions (`__InstanceModificationEvent`) can be unreliable — they may stop firing after the first event, or not fire at all on some Windows configurations.

**Solution:** The event watcher is treated as a "bonus" real-time trigger. The 30-second polling loop is the primary detection mechanism. If WMI events fail, a warning is logged and the service continues with polling only. The watcher thread is fire-and-forget within `WatchPrintEvents()`.

### 8.8 Avoiding Infinite Recovery Loops

**Problem:** If a printer is genuinely broken (hardware failure), the service could keep recovering it forever, wasting CPU and potentially disrupting other users.

**Solution:** Three independent safeguards:
1. **Per-job dedup:** Each `PrinterName:JobId` is tracked for 10 minutes after alerting — no repeat recovery for the same stuck job
2. **Cooldown:** 10 minutes between any recovery cycles
3. **Rate limit:** Max 3 recoveries per hour, hard cap
4. **Manual pause:** Admin can pause monitoring for 30 minutes via tray icon

### 8.9 App Restart After Auto-Update

**Problem:** After downloading a new version, the app needs to restart to use the new binary. The old approach used `ServiceController.Stop()` + `Start()`, but that only works for Windows Services.

**Solution:** The auto-updater launches a new process with `Process.Start(Verb="runas")` (for admin elevation), then calls `Environment.Exit(0)`. The new process loads the updated binary from the install directory. This works for any app model (service, tray, console) and doesn't depend on SCM.

### 8.10 GitHub Token Cleanup

**Problem:** The GitHub personal access token was temporarily embedded in the remote URL for push access. If left there, it would be exposed in `git remote -v` output.

**Solution:** After the push succeeded, the remote URL was updated to the HTTPS form without the token:
```powershell
git remote set-url origin https://github.com/BobanAliBrz/PrinterResetAliBrz.git
```

### 8.11 Large ZIP in Git History

**Problem:** The v1.0.0 release ZIP (66 MB) was accidentally committed to the repo. GitHub warns at 50 MB and can reject pushes.

**Solution:** Added `*.zip` and `publish/` to `.gitignore`, then removed the tracked files with `git rm --cached`. The publish directory was already excluded from future commits. Note: the ZIP still exists in git history but is no longer tracked.

### 8.12 Programmatic Printer Icon (No .ico File Needed)

**Problem:** The tray icon was `SystemIcons.Information` — the generic blue "i" circle. Creating a proper .ico file requires a designer tool, embedding it as a resource, and maintaining it. For a utility tool, this is overhead.

**Solution:** Drew a 16x16 printer icon with GDI+ at runtime in `IconHelper.cs`:
- Paper feeding in from the top (white rectangle with subtle lines)
- Dark gray printer body with rounded corners
- Inset paper output slot (darker shade)
- Green status LED dot with a highlight pixel
- Subtle top-edge reflection

The bitmap is kept alive via a static field because `Icon.FromHandle()` wraps a GDI handle owned by the bitmap — disposing the bitmap would invalidate the icon handle.

### 8.13 Migrating from Windows Service to Startup Folder

**Problem:** The app was originally installed as a Windows Service (`sc.exe create`), but it was a tray app with `NotifyIcon` under the hood — not a proper `ServiceBase`-derived service. SCM expected a service control handler that never registered, causing:
- Event ID 7000 errors ("service failed to start")
- Duplicate instances (failure recovery restarts the "failed" service, stacking processes)
- Confusing logs for IT admins

**Solution:** Switched to `Environment.SpecialFolder.CommonStartup` (All Users Startup folder). Benefits:
- No SCM involvement — no false errors, no duplicate instances
- App runs as the logged-in user (with UAC elevation from the manifest)
- Simpler deployment (create a .lnk instead of `sc.exe` + failure recovery config)
- Same admin-elevated behavior via `requireAdministrator` in the manifest
- Built-in self-registration: `Program.RegisterStartup()` creates the shortcut on first admin run

Migration path: deploy script and bootstrapper both check for an existing service and clean it up with `sc.exe delete` before creating the startup shortcut.

---

## 9. Known Issues & Edge Cases

| Issue | Status |
|---|---|
| Windows 7 compatibility | Tested with `net6.0-windows` self-contained publish. .NET 6.0 extended support ended, but the published binary is standalone. If Win7 issues arise, the target can be rolled back to `net48`. |
| HP M404dns / P1000 series specifics | No special handling needed — they behave as standard USB printers. |
| 30-second polling delay | Intentional — avoids hammering WMI on old Celeron E3300 PCs. |
| Bootstrapper not yet built | The `Bootstrapper` project targets `net48` and hasn't been compiled. It needs a machine with .NET Framework 4.8 build tools (VS or MSBuild). The main service was published self-contained with .NET SDK on this dev machine. |
| `app.config` startup section | Currently references `net48` (`supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"`). Since the main app is now `net6.0-windows`, this stanza is unused by the runtime. Can be removed but harmless. |
| UAC prompt on login | Because `app.manifest` requests `requireAdministrator`, users get a UAC prompt when the startup shortcut launches. This is expected — the tool needs admin rights to restart the spooler and reset USB devices. In an IT environment where users are local admins, this is a single click-through. |

---

## 10. Configuration Tips for IT Admins

- **Zero-config default:** Just install and it works — watches all printers automatically.
- **To target specific printers only:** Set `WatchedPrinters` to `"HP LaserJet;Brother"` (semicolon-separated, partial name match).
- **To disable auto-updates:** Set `UpdateCheckIntervalHours` to `0`.
- **Logs are at:** `C:\ProgramData\PrintSpoolerGuardian\PrintSpoolerGuardian.log`
- **Recovery is conservative by design.** If printers fail faster than 3 times per hour, increase `MaxRecoveriesPerHour`.
- **After a reboot**, the app starts automatically for every user via the All Users Startup folder shortcut.

---

## 11. File Templates & Patterns

### Adding a new recovery action

1. Create the action class in `Services/` or `Engine/`
2. Add the call in `RecoveryEngine.ExecuteUsbRecoveryAsync()` or `ExecuteSharedRecoveryAsync()`
3. Add any new config keys to `app.config` with safe defaults
4. Wire logging via `Logger.Info()` / `Logger.Error()`

### Adding a new WMI query

All WMI queries live in `PrintJobDetector.cs`. Use `ManagementObjectSearcher` with WQL. Always wrap in try/catch with `Logger.Error()`.

### Handling the `app.config` connection type mismatch

The `app.config` currently uses `<printGuardian>` section with `NameValueSectionHandler` — this is a legacy .NET Framework pattern. In .NET 6.0, `ConfigurationManager` still reads it correctly via `ConfigurationManager.AppSettings`, so it works. If migrating to a newer configuration system (e.g., `Microsoft.Extensions.Configuration`), convert to JSON or XML-based config.

---

## 12. Release Process

```powershell
# 1. Bump version in PrintSpoolerGuardian.csproj (AssemblyVersion/FileVersion)
# 2. Build
dotnet publish -c Release -r win-x64 --self-contained -o .\publish

# 3. Create ZIP
Compress-Archive -Path .\publish\* -DestinationPath PrintSpoolerGuardian_vX.X.X.zip

# 4. Create GitHub Release + upload ZIP
# The auto-updater regex matches [._]v?(\d+\.\d+(?:\.\d+)*) in the filename
# So "PrintSpoolerGuardian_v1.2.3.zip" → version "1.2.3" ✓

# 5. Tag and push
git tag v1.2.3
git push origin v1.2.3
```

All deployed PCs will auto-update within 24 hours (or 5 minutes after service start on next boot).

---

## 13. Future Considerations (Not Implemented)

- **Notification system** (email/toast on failure) — explicitly not requested
- **HTTP health endpoint** for monitoring dashboards
- **Watchdog mode** — restart service if spooler crashes but printer is fine
- **Per-printer recovery statistics** — how often each printer triggers recovery
- **Graceful degradation** — if WMI is completely unavailable, fall back to polling spool directory
