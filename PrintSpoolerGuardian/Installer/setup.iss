; Print Spooler Guardian — Inno Setup Installer
; Build: ISCC.exe setup.iss

#define MyAppName "Print Spooler Guardian"
#define MyAppShortName "PrintSpoolerGuardian"
#define MyAppPublisher "BobanAliBrz"
#define MyAppURL "https://github.com/BobanAliBrz/PrinterResetAliBrz"
#define MyAppExeName "PrintSpoolerGuardian.exe"

; Version is passed via /dMyAppVersion= from build.ps1, or default here
#ifndef MyAppVersion
  #define MyAppVersion "1.2.1.0"
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
; Support the oldest Win7 builds (no Service Pack required).
; 6.1 = Windows 7. We no longer require SP1 so early Win7 builds are accepted.
MinVersion=6.1
; .NET Framework 4.8 is REQUIRED (the app runs on net48). The InitializeSetup
; function below checks for it and offers to download/install it automatically.

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
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: postinstall nowait skipifsilent runascurrentuser shellexec; Verb: runas

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "/uninstall"; Flags: runhidden; RunOnceId: "StopGuardian"

[Code]
function CheckNet48(): Boolean;
var
  RegValue: Cardinal;
begin
  // .NET Framework 4.8 has Release registry value >= 528449
  if RegQueryDWordValue(HKLM, 'SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full', 'Release', RegValue) then
  begin
    if RegValue >= 528449 then
    begin
      Result := True;
      Exit;
    end;
  end;
  Result := False;
end;

function InstallNet48(): Boolean;
var
  TmpFile, Url: string;
  ResultCode: Integer;
begin
  Result := False;
  Url := 'https://go.microsoft.com/fwlink/?linkid=2088631';
  TmpFile := ExpandConstant('{tmp}') + '\ndp48-web.exe';
  // Use certutil (built into Windows 7+) to download — avoids Inno download API quirks
  if not Exec('certutil.exe', '-urlcache -split -f "' + Url + '" "' + TmpFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Could not download .NET Framework 4.8.' + #13#10 +
           'Please install it manually from https://dotnet.microsoft.com/download/dotnet-framework/net48 ' +
           'and re-run this setup.', mbError, MB_OK);
    Exit;
  end;
  if not FileExists(TmpFile) then
  begin
    MsgBox('Could not download .NET Framework 4.8 (file missing).' + #13#10 +
           'Please install it manually and re-run this setup.', mbError, MB_OK);
    Exit;
  end;
  // /q quiet, /norestart so the installer can finish; user reboots after.
  if not Exec(TmpFile, '/q /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to launch the .NET Framework 4.8 installer.', mbError, MB_OK);
    Exit;
  end;
  // 3010 = success, reboot required. 0 = success.
  Result := (ResultCode = 0) or (ResultCode = 3010);
end;

function InitializeSetup: Boolean;
begin
  if not CheckNet48() then
  begin
    if MsgBox('Print Spooler Guardian requires .NET Framework 4.8, which is not installed.' + #13#10 +
              'Click OK to download and install it now (a reboot may be required afterwards).',
              mbInformation, MB_OKCANCEL) = IDOK then
    begin
      if not InstallNet48() then
      begin
        Result := False;
        Exit;
      end;
      // Re-check after install attempt
      if not CheckNet48() then
      begin
        MsgBox('.NET Framework 4.8 is still not detected. Please reboot and re-run the setup.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
    end
    else
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Log installation to app's log directory
    SaveStringToFile(ExpandConstant('{commonappdata}\PrintSpoolerGuardian\install.log'),
      'Installed version ' + ExpandConstant('{#MyAppVersion}') + ' on ' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + #13#10,
      True);
  end;
end;
