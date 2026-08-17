; Print Spooler Guardian — Inno Setup Installer
; Build: ISCC.exe setup.iss

#define MyAppName "Print Spooler Guardian"
#define MyAppShortName "PrintSpoolerGuardian"
#define MyAppPublisher "BobanAliBrz"
#define MyAppURL "https://github.com/BobanAliBrz/PrinterResetAliBrz"
#define MyAppExeName "PrintSpoolerGuardian.exe"

; Version is passed via /dMyAppVersion= from build.ps1, or default here
#ifndef MyAppVersion
  #define MyAppVersion "2.3.0.0"
#endif

[Setup]
AppId={{B7A3E8C1-5F2D-4A9B-8E6C-3D1F0A7B2C5E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} — Printer Auto-Recovery

DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\..\dist
OutputBaseFilename=PrintSpoolerGuardian_Setup_v{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
; Allow both 32-bit and 64-bit installers to run on either architecture.
; (We ship a single installer that contains both x86 and x64 binaries.)
ArchitecturesInstallIn64BitMode=x64os
ArchitecturesAllowed=x64 x86
DisableProgramGroupPage=yes
CloseApplications=yes
; Support Windows 7 SP1 and later (6.1sp1 = Win7 SP1).
; The app targets Windows 7's inbox .NET Framework 3.5.1, so no runtime
; installer or machine-wide prerequisite is required.
MinVersion=6.1sp1

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
; x64 binaries (installed on 64-bit systems)
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: Is64BitInstallMode
; x86 binaries (installed on 32-bit systems)
Source: "..\publish\win-x86\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not Is64BitInstallMode

; Always install the ReadMe
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion; DestName: "README.txt"

[Dirs]
Name: "{app}"; Permissions: users-modify

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Monitor and recover stuck printers"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; WorkingDir: "{app}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "Print Spooler Guardian — printer auto-recovery"

; Register in All Users Startup folder (auto-start for every user)
Name: "{commonstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Print Spooler Guardian"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: postinstall nowait skipifsilent runascurrentuser shellexec; Verb: runas; Check: ShouldLaunchApplication

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "/uninstall"; Flags: runhidden; RunOnceId: "StopGuardian"

[Code]
function ShouldLaunchApplication(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Log installation to app's log directory
    ForceDirectories(ExpandConstant('{commonappdata}\PrintSpoolerGuardian'));
    SaveStringToFile(ExpandConstant('{commonappdata}\PrintSpoolerGuardian\install.log'),
      'Installed version ' + ExpandConstant('{#MyAppVersion}') + ' on ' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + #13#10,
      True);
  end;
end;
