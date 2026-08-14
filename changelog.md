# Changelog

All notable changes to Print Spooler Guardian are recorded here.

## [Unreleased]

## [2.0.2.0] - 2026-08-14

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
