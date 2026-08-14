# Changelog

All notable changes to Print Spooler Guardian are recorded here.

## [Unreleased]

## [2.1.0.0] - 2026-08-14

### Changed

- Switched the application runtime from unsupported .NET 8 to .NET Framework 4.8 for Windows 7 SP1 through Windows 11 compatibility.
- Bundle the official Microsoft .NET Framework 4.8 offline installer and install it only when the target PC does not already have it.

### Fixed

- Avoid the Windows 7 `hostfxr.dll` startup failure that prevented .NET 8 builds from launching.

## [2.0.2.0] - 2026-08-14 (unreleased)

### Fixed

- Bundle the full architecture-matched Universal CRT beside the application so it launches on Windows 7 machines without the VC++ runtime or KB2999226 installed.

### Documentation

- Added this change log and a concise maintainer reference.

## [2.0.1.0] - 2026-08-02

### Fixed

- Removed the invalid Common-Controls manifest `publicKeyToken` that caused Windows 7 32-bit side-by-side configuration error 14001.

## [2.0.0.0] - 2026-07-21

### Changed

- Migrated to self-contained .NET 8 builds, eliminating the .NET Framework 4.8 prerequisite.
- Added partial trimming with roots for WMI and configuration assemblies.
- Replaced the legacy bootstrapper with a unified Inno Setup installer that includes x86 and x64 builds.

## [1.1.0] - 2026-05-13

### Changed

- Replaced Windows Service installation with an All Users Startup shortcut, matching the tray-app design.
- Added a programmatic printer tray icon.

## [1.0.0]

### Added

- Initial USB and shared-printer monitoring and automatic recovery.
