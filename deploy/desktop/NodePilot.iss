; NodePilot desktop installer (Inno Setup 6).
; Machine-wide, offline, Windows 11 x64. Built by deploy/desktop/Build-DesktopInstaller.ps1,
; which passes /DStageDir, /DAppVersion, /DOutputDir. Electron (Chromium+Node) is shipped in
; full inside StageDir\desktop; no WebView2, no runtime prerequisites, no auto-update.

#ifndef StageDir
  #define StageDir "stage"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "out"
#endif

[Setup]
AppId={{7E2D2C5A-8C3F-4E9B-9D21-A1B2C3D4E5F6}
AppName=NodePilot
AppVersion={#AppVersion}
AppPublisher=NodePilot
AppPublisherURL=https://github.com/Sev7eNup/NodePilot
DefaultDirName={autopf}\NodePilot
DefaultGroupName=NodePilot
DisableProgramGroupPage=yes
UninstallDisplayName=NodePilot
UninstallDisplayIcon={app}\desktop\NodePilot.exe
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; Windows 11 (build 22000) or later only.
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=NodePilot-Desktop-Setup-{#AppVersion}
WizardStyle=modern
SetupIconFile={#StageDir}\setup-icon.ico

[Files]
Source: "{#StageDir}\app\*";     DestDir: "{app}\app";     Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\desktop\*"; DestDir: "{app}\desktop"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\pgsql\*";   DestDir: "{app}\pgsql";   Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\deploy\*";  DestDir: "{app}\deploy";  Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\NodePilot";         Filename: "{app}\desktop\NodePilot.exe"
Name: "{commondesktop}\NodePilot"; Filename: "{app}\desktop\NodePilot.exe"

[Run]
; Provision the local runtime (elevated): Postgres cluster+service, cert, config, API service,
; desktop.json, first-run token handoff. Idempotent — safe on reinstall.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\Provision-LocalDb.ps1"" -InstallPath ""{app}"""; \
  StatusMsg: "Provisioning local database and services (this can take a minute)..."; \
  Flags: runhidden waituntilterminated
; Launch the shell as the interacting user (installer runs elevated).
Filename: "{app}\desktop\NodePilot.exe"; \
  Description: "Launch NodePilot"; \
  Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Stop + delete both services and remove the loopback certificate. ProgramData / pgdata are
; preserved by the script unless -PurgeData is passed (not exposed in the UI uninstaller).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\Uninstall-Desktop.ps1"" -InstallPath ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "NodePilotDesktopUninstall"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  PgVersion: String;
  UpdateScript: String;
begin
  // Before overwriting binaries on an upgrade, take an ACL-protected pg_dump so a failed update
  // can be recovered. Detected by an existing cluster; best-effort (never blocks the install).
  if CurStep = ssInstall then
  begin
    PgVersion := ExpandConstant('{commonappdata}\NodePilot\pgdata\PG_VERSION');
    UpdateScript := ExpandConstant('{app}\deploy\Update-Desktop.ps1');
    if FileExists(PgVersion) and FileExists(UpdateScript) then
    begin
      Exec('powershell.exe',
        '-NoProfile -ExecutionPolicy Bypass -File "' + UpdateScript + '" -InstallPath "' + ExpandConstant('{app}') + '" -BackupOnly',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
