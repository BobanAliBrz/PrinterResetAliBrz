<#
.SYNOPSIS
    Builds Print Spooler Guardian for win-x64 and win-x86, and compiles the installer.
.DESCRIPTION
    1. Reads the version from PrintSpoolerGuardian.csproj
    2. Publishes self-contained for win-x64 (trimmed)
    3. Publishes self-contained for win-x86 (trimmed)
    4. Compiles Inno Setup installer (.exe) containing both architectures
.PARAMETER Configuration
    Build configuration: Release (default) or Debug
.PARAMETER SkipBuild
    Skip the dotnet publish step (use existing build)
.PARAMETER SkipInstaller
    Skip the Inno Setup compile step (only build binaries)
.EXAMPLE
    .\Installer\build.ps1
.EXAMPLE
    .\Installer\build.ps1 -Configuration Debug
.EXAMPLE
    .\Installer\build.ps1 -SkipBuild
#>

param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Resolve-Path "$PSScriptRoot/.."
$PublishDir = "${ProjectRoot}/publish"
$InstallerExe = "${env:LOCALAPPDATA}/InnoSetup6/ISCC.exe"
$DistDir = Resolve-Path "${ProjectRoot}/.." # parent of project = repo root

function Log($msg) {
    $ts = Get-Date -Format "HH:mm:ss"
    Write-Host "[$ts] $msg"
}

function Get-ProjectVersion {
    $csproj = "${ProjectRoot}/PrintSpoolerGuardian.csproj"
    $xml = [xml](Get-Content $csproj)
    $version = $xml.Project.PropertyGroup.Version
    if ([string]::IsNullOrEmpty($version)) {
        throw "Could not find <Version> in $csproj"
    }
    return $version.Trim()
}

function Publish-App($rid) {
    Log "Publishing $rid ($Configuration, self-contained + trimmed)..."
    $outDir = "${PublishDir}/${rid}"

    if (Test-Path $outDir) {
        Remove-Item $outDir -Recurse -Force
        Log "  Cleaned previous publish: $outDir"
    }

    # Self-contained + trimmed (partial) publish.
    # This bundles the entire .NET runtime into the output so target PCs
    # need ZERO prerequisites — no .NET Framework, no .NET Runtime, nothing.
    # TrimMode=partial only trims assemblies that opted in, avoiding breakage
    # with WMI (System.Management) and WinForms which use heavy reflection.
    $p = Start-Process -FilePath "dotnet" -ArgumentList @(
        "publish",
        "${ProjectRoot}/PrintSpoolerGuardian.csproj",
        "-c", $Configuration,
        "-r", $rid,
        "--self-contained", "true",
        "-o", $outDir,
        "-p:PublishTrimmed=true",
        "-p:TrimMode=partial",
        "-p:DebugType=none",
        "-p:DebugSymbols=false"
    ) -NoNewWindow -Wait -PassThru

    if ($p.ExitCode -ne 0) {
        throw "dotnet publish for $rid failed with exit code $($p.ExitCode)"
    }

    $exePath = "${outDir}/PrintSpoolerGuardian.exe"
    if (!(Test-Path $exePath)) {
        throw "Build succeeded but $exePath not found!"
    }

    $totalSize = (Get-ChildItem $outDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
    $count = (Get-ChildItem $outDir -File -Recurse | Measure-Object).Count
    Log "  OK - $rid build: ${count} files, total $([math]::Round($totalSize, 1)) MB"
    return $outDir
}

function Add-UniversalCrt([string]$Rid, [string]$Destination) {
    # Self-contained .NET still uses the Windows Universal CRT. Windows 10+
    # provides it, but untouched Windows 7 installations commonly do not.
    # App-local deployment avoids installing a machine-wide VC++ runtime.
    $architecture = switch ($Rid) {
        "win-x86" { "x86" }
        "win-x64" { "x64" }
        default { throw "No Universal CRT architecture mapping for runtime identifier: $Rid" }
    }

    $candidateRoots = @()
    if (-not [string]::IsNullOrWhiteSpace($env:WindowsSdkDir)) {
        $candidateRoots += Join-Path $env:WindowsSdkDir "Redist\ucrt\DLLs"
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidateRoots += "${env:ProgramFiles(x86)}\Windows Kits\10\Redist\ucrt\DLLs"
    }

    $source = $candidateRoots |
        ForEach-Object { Join-Path $_ $architecture } |
        Where-Object {
            (Test-Path (Join-Path $_ "ucrtbase.dll")) -and
            (Test-Path (Join-Path $_ "api-ms-win-crt-runtime-l1-1-0.dll"))
        } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($source)) {
        throw "Universal CRT redistributable files for $architecture were not found. Install the Windows 10 SDK, then rebuild."
    }

    $files = Get-ChildItem -Path $source -Filter "*.dll" -File
    if ($files.Count -eq 0) {
        throw "No Universal CRT DLLs were found in $source"
    }

    Copy-Item -Path (Join-Path $source "*.dll") -Destination $Destination -Force
    Log "  Included $($files.Count) app-local Universal CRT DLLs for $architecture"
}

function Compile-Installer($version) {
    if (!(Test-Path $InstallerExe)) {
        throw "Inno Setup ISCC.exe not found at $InstallerExe"
    }

    Log "Compiling installer with Inno Setup..."

    $issPath = "${PSScriptRoot}/setup.iss"

    # Ensure dist directory exists (repo root/dist)
    $distDir = "${DistDir}/dist"
    if (!(Test-Path $distDir)) {
        New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    }

    $p = Start-Process -FilePath $InstallerExe -ArgumentList @(
        "/dMyAppVersion=${version}",
        "/Q",
        $issPath
    ) -NoNewWindow -Wait -PassThru

    if ($p.ExitCode -gt 1) {
        throw "Inno Setup compilation failed with exit code $($p.ExitCode)"
    }

    # Find the output installer
    $installerPattern = "PrintSpoolerGuardian_Setup_v${version}.exe"
    $installerPath = "${distDir}/${installerPattern}"

    if (!(Test-Path $installerPath)) {
        $found = Get-ChildItem $distDir -Filter "PrintSpoolerGuardian_Setup_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($found) {
            $installerPath = $found.FullName
        } else {
            throw "Installer not found in ${distDir}"
        }
    }

    $size = (Get-Item $installerPath).Length / 1MB
    Log "  OK - Installer compiled: $installerPath ($([math]::Round($size, 1)) MB)"
    return $installerPath
}

# ========== MAIN ==========
Write-Host ""
Write-Host "Print Spooler Guardian - Build Script" -ForegroundColor Cyan
Write-Host "  Self-contained .NET 8 (no prerequisites on target)" -ForegroundColor DarkCyan
Write-Host ""

$version = Get-ProjectVersion
Log "Project version: ${version}"
Log "Project root:    ${ProjectRoot}"
Log ""

$buildDirs = @()

if (-not $SkipBuild) {
    $buildDirs += Publish-App "win-x64"
    $buildDirs += Publish-App "win-x86"
} else {
    Log "Skipping build - using existing publish directories"
    $x64 = "${PublishDir}/win-x64"
    $x86 = "${PublishDir}/win-x86"
    if (Test-Path $x64) { $buildDirs += $x64 }
    if (Test-Path $x86) { $buildDirs += $x86 }
    if ($buildDirs.Count -eq 0) {
        throw "No existing builds found in ${PublishDir}"
    }
}

foreach ($buildDir in $buildDirs) {
    Add-UniversalCrt -Rid (Split-Path $buildDir -Leaf) -Destination $buildDir
}

if (-not $SkipInstaller) {
    $installer = Compile-Installer $version
    Log ""
    Log "======= BUILD COMPLETE ======="
    Log "Version: ${version}"
    Log "Installer: ${installer}"
    Log "x64: ${PublishDir}/win-x64"
    Log "x86: ${PublishDir}/win-x86"
    Log "=============================="
    Write-Host ""
    Write-Host "INSTALLER: ${installer}" -ForegroundColor Green
} else {
    Log ""
    Log "=== BUILD COMPLETE (no installer) ==="
    Log "x64: ${PublishDir}/win-x64"
    Log "x86: ${PublishDir}/win-x86"
}
