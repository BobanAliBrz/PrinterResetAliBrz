<#
.SYNOPSIS
    Mass-deploys Print Spooler Guardian via SCCM, PDQ, Intune, or manual run.

.DESCRIPTION
    Checks prerequisites (.NET 4.8), installs Print Spooler Guardian as a
    Windows Service, configures auto-update (if GitHub repo specified),
    and creates a desktop shortcut.

    Supports both interactive and silent/unattended modes.

.PARAMETER Silent
    Runs without any UI. Suitable for SCCM/PDQ/Intune deployment.

.PARAMETER GitHubRepo
    Override the GitHub repo for auto-updates (format: Owner/Repo).
    If not specified, reads from app.config or leaves it blank.

.PARAMETER InstallDir
    Override the installation directory (default: C:\ProgramData\PrintSpoolerGuardian).

.EXAMPLE
    # Interactive install (double-click or run without params)
    .\deploy.ps1

.EXAMPLE
    # Silent install via SCCM/PDQ/Intune
    .\deploy.ps1 -Silent

.EXAMPLE
    # Silent install with custom GitHub repo for auto-updates
    .\deploy.ps1 -Silent -GitHubRepo "MyCompany/PrintSpoolerGuardian"
#>

param(
    [switch]$Silent = $false,
    [string]$GitHubRepo = "",
    [string]$InstallDir = "C:\ProgramData\PrintSpoolerGuardian"
)

$ErrorActionPreference = "Stop"
$ServiceName = "PrintSpoolerGuardian"
$RequiredNetVersion = "4.8"

function Log($msg) {
    $ts = Get-Date -Format "HH:mm:ss"
    Write-Host "[$ts] $msg"
    if (!$Silent) { $host.UI.RawUI.FlushInputBuffer() }
}

function Check-NetFramework {
    Log "Checking .NET Framework $RequiredNetVersion..."
    $key = "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"
    if (Test-Path $key) {
        $release = (Get-ItemProperty $key).Release
        if ($release -ge 528449) {
            Log "  ✓ .NET Framework $RequiredNetVersion is installed"
            return $true
        }
    }
    Log "  ✗ .NET Framework $RequiredNetVersion NOT found"
    return $false
}

function Install-NetFramework {
    Log "Downloading and installing .NET Framework $RequiredNetVersion..."
    $url = "https://go.microsoft.com/fwlink/?linkid=2088631"
    $installer = "$env:TEMP\ndp48-web.exe"

    try {
        (New-Object System.Net.WebClient).DownloadFile($url, $installer)
        Log "  Running installer (this may take several minutes)..."
        if ($Silent) {
            Start-Process -FilePath $installer -Args "/q /norestart" -Wait -PassThru
        } else {
            Start-Process -FilePath $installer -Args "/passive /norestart" -Wait -PassThru
        }

        if (Check-NetFramework) {
            Log "  ✓ .NET Framework installed successfully"
            return $true
        } else {
            Log "  ⚠ .NET installation may require a reboot to complete."
            Log "     Please reboot and re-run this script."
            return $false
        }
    } catch {
        Log "  ✗ Failed to install .NET Framework: $_"
        return $false
    } finally {
        if (Test-Path $installer) { Remove-Item $installer -ErrorAction SilentlyContinue }
    }
}

function Download-Release {
    # Check if local build exists (for offline/deploy from network share)
    $localExe = Join-Path $PSScriptRoot "PrintSpoolerGuardian\bin\Release\PrintSpoolerGuardian.exe"
    if (Test-Path $localExe) {
        Log "Found locally built executable. Using local build."
        return $PSScriptRoot
    }

    # Otherwise download from GitHub
    Log "Downloading latest release from GitHub..."

    # Try to get latest release URL
    $repo = $GitHubRepo
    if ([string]::IsNullOrEmpty($repo)) {
        # Read from app.config if available
        $configPath = Join-Path $PSScriptRoot "PrintSpoolerGuardian\app.config"
        if (Test-Path $configPath) {
            $xml = [xml](Get-Content $configPath)
            $node = $xml.SelectSingleNode("//add[@key='UpdateGitHubRepo']")
            if ($node -and $node.Value) { $repo = $node.Value }
        }
    }

    if ([string]::IsNullOrEmpty($repo)) {
        Log "  ERROR: No GitHub repository configured for download."
        Log "  Set the UpdateGitHubRepo value in app.config, or use -GitHubRepo parameter."
        return $null
    }

    try {
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add("User-Agent", "PrintSpoolerGuardian-Deploy")
        $apiUrl = "https://api.github.com/repos/$repo/releases/latest"
        Log "  Querying: $apiUrl"
        $json = $wc.DownloadString($apiUrl)

        $match = [regex]::Match($json, '"browser_download_url"\s*:\s*"([^"]*\.zip)"')
        if (!$match.Success) {
            Log "  ERROR: Could not find download URL in release response."
            return $null
        }

        $downloadUrl = $match.Groups[1].Value
        $zipName = [System.IO.Path]::GetFileName($downloadUrl)
        $zipPath = Join-Path $env:TEMP $zipName

        Log "  Downloading: $zipName"

        # Show progress
        $wc.DownloadProgressChanged += {
            param($sender, $e)
            Write-Progress -Activity "Downloading Print Spooler Guardian" `
                -Status "$($e.ProgressPercentage)%" -PercentComplete $e.ProgressPercentage
        }
        $wc.DownloadFileCompleted += {
            param($sender, $e)
            Write-Progress -Activity "Downloading Print Spooler Guardian" -Completed
        }

        [System.Net.ServicePointManager]::SecurityProtocol =
            [System.Net.SecurityProtocolType]::Tls12
        $wc.DownloadFile($downloadUrl, $zipPath)

        Log "  Extracting to $InstallDir..."
        if (Test-Path $InstallDir) {
            Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

        try {
            Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
        } catch {
            # Fallback for PowerShell 4 (Win7) which lacks -Force
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $InstallDir)
        }

        Remove-Item $zipPath -ErrorAction SilentlyContinue
        Log "  ✓ Download and extraction complete"
        return $InstallDir
    } catch {
        Log "  ✗ Download failed: $_"
        return $null
    }
}

function Update-AppConfig {
    param([string]$dir)

    $configPath = Join-Path $dir "app.config"
    if (!(Test-Path $configPath)) { return }

    $xml = [xml](Get-Content $configPath)

    # Update GitHub repo if provided
    if ($GitHubRepo) {
        $node = $xml.SelectSingleNode("//add[@key='UpdateGitHubRepo']")
        if ($node) { $node.SetAttribute("value", $GitHubRepo) }
        Log "  Updated UpdateGitHubRepo to: $GitHubRepo"
    }

    # Enable auto-update by default (check every 24h)
    $updateNode = $xml.SelectSingleNode("//add[@key='UpdateCheckIntervalHours']")
    if ($updateNode -and $updateNode.Value -eq "0") {
        $updateNode.SetAttribute("value", "24")
        Log "  Enabled auto-update checks (24h interval)"
    }

    $xml.Save($configPath)
}

function Install-Service {
    param([string]$dir)

    $exePath = Join-Path $dir "PrintSpoolerGuardian.exe"
    if (!(Test-Path $exePath)) {
        Log "  ERROR: Could not find PrintSpoolerGuardian.exe in $dir"
        return $false
    }

    # Uninstall existing service first
    Log "Removing existing service (if any)..."
    & sc.exe delete $ServiceName 2>$null
    Start-Sleep 2

    # Install service
    Log "Installing Windows Service..."
    $result = & sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto
    if ($LASTEXITCODE -ne 0) {
        Log "  ✗ Service creation failed: $result"
        return $false
    }

    # Set failure recovery
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 2>$null
    & sc.exe description $ServiceName "Monitors USB printers and auto-recovers stuck print jobs" 2>$null

    Log "  ✓ Service installed"

    # Start service
    Log "Starting service..."
    try {
        Start-Service $ServiceName -ErrorAction Stop
        Start-Sleep 3
        $svc = Get-Service $ServiceName
        if ($svc.Status -eq 'Running') {
            Log "  ✓ Service is running"
            return $true
        } else {
            Log "  ⚠ Service status: $($svc.Status)"
            return $true
        }
    } catch {
        Log "  ⚠ Could not start service: $_. The service is installed and will auto-start on reboot."
        return $true
    }
}

function Create-Shortcut {
    param([string]$dir)

    $target = Join-Path $dir "PrintSpoolerGuardian.exe"
    $shortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "Print Spooler Guardian.lnk"

    try {
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($shortcutPath)
        $sc.TargetPath = $target
        $sc.Description = "Print Spooler Guardian - USB printer monitoring"
        $sc.IconLocation = "$target,0"
        $sc.Save()
        [System.Runtime.Interopservices.Marshal]::ReleaseComObject($sc) | Out-Null
        [System.Runtime.Interopservices.Marshal]::ReleaseComObject($ws) | Out-Null
        Log "  ✓ Desktop shortcut created"
    } catch {
        Log "  ⚠ Could not create shortcut: $_"
    }
}

# ========== MAIN ==========
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = Get-Location }

if (!$Silent) {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║     🖨  Print Spooler Guardian — Deploy Script         ║" -ForegroundColor Cyan
    Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host ""
}

# Step 1: Check/install .NET
if (!(Check-NetFramework)) {
    if (!(Install-NetFramework)) {
        Log "ERROR: .NET Framework installation failed. Cannot continue."
        if (!$Silent) { Read-Host "Press Enter to exit" }
        exit 1
    }
    Log "Please re-run this script after reboot completes."
    if (!$Silent) { Read-Host "Press Enter to exit" }
    exit 0
}

# Step 2: Download/install files
$installDir = Download-Release
if (!$installDir) {
    Log "ERROR: Could not download or locate installation files."
    if (!$Silent) { Read-Host "Press Enter to exit" }
    exit 1
}

# Step 3: Update config
Update-AppConfig -dir $installDir

# Step 4: Install service
if (!(Install-Service -dir $installDir)) {
    Log "ERROR: Service installation failed."
    if (!$Silent) { Read-Host "Press Enter to exit" }
    exit 1
}

# Step 5: Create shortcut (skip in silent mode)
if (!$Silent) {
    Create-Shortcut -dir $installDir
}

Write-Host ""
Log "═══════════════════════════════════════════════════════════"
Log "  ✓ DEPLOYMENT COMPLETE"
Log "  Service:    $ServiceName"
Log "  Location:   $InstallDir"
Log "  Log file:   $InstallDir\PrintSpoolerGuardian.log"
Log "═══════════════════════════════════════════════════════════"
Log ""
Log "The service will monitor ALL USB printers automatically."
Log "Edit $(Join-Path $InstallDir 'app.config') to customize settings."

if (!$Silent) {
    Read-Host "Press Enter to exit"
}