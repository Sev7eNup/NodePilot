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

[Tasks]
; The desktop shortcut is the one optional part of this install; the Start-Menu entry is created
; unconditionally. The description is literal English rather than {cm:CreateDesktopIcon}, because
; this script declares no [Languages] section.
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#StageDir}\app\*";     DestDir: "{app}\app";     Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\desktop\*"; DestDir: "{app}\desktop"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\pgsql\*";   DestDir: "{app}\pgsql";   Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\deploy\*";  DestDir: "{app}\deploy";  Flags: recursesubdirs createallsubdirs ignoreversion
; Operator clients: `np` drives the installation from a script, `nodepilot-mcp` is what an AI
; agent connects to. Shipped because a desktop user has no build toolchain to produce them.
Source: "{#StageDir}\tools\*";   DestDir: "{app}\tools";   Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\NodePilot";         Filename: "{app}\desktop\NodePilot.exe"
Name: "{commondesktop}\NodePilot"; Filename: "{app}\desktop\NodePilot.exe"; Tasks: desktopicon

[Run]
; NOTE: provisioning is not a [Run] entry. [Run] discards the exit code, so a failed provisioning
; would still report a successful install. It runs from CurStepChanged below, where ResultCode can
; be inspected. See ProvisionRuntime().
; Launches the shell as the interacting user (the installer runs elevated), and only when
; provisioning succeeded, so the user is not shown a second dialog for a problem already reported.
Filename: "{app}\desktop\NodePilot.exe"; \
  Description: "Launch NodePilot"; \
  Check: ProvisionSucceeded; \
  Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Stop + delete both services and remove the loopback certificate. ProgramData / pgdata are
; preserved by the script unless -PurgeData is passed (not exposed in the UI uninstaller).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\Uninstall-Desktop.ps1"" -InstallPath ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "NodePilotDesktopUninstall"

[Code]
var
  ProvisionOk: Boolean;

// Guards the "Launch NodePilot" post-install entry.
function ProvisionSucceeded(): Boolean;
begin
  Result := ProvisionOk;
end;

// Runs the elevated provisioning script and REPORTS ITS EXIT CODE. Previously this was a [Run]
// entry, which throws the exit code away: a provisioning that aborted (port pool exhausted, the
// API service never reaching /healthz/ready, a cluster whose secrets went missing) still produced
// a "Setup completed successfully" page, and the first thing the user saw was the shell failing
// to connect. Deliberately does NOT abort setup - the files are already in place and a rollback
// here would delete a database the user may still want. It reports plainly and names the log.
procedure ProvisionRuntime();
var
  ResultCode: Integer;
  ProvisionScript: String;
  LogPath: String;
begin
  ProvisionScript := ExpandConstant('{app}\deploy\Provision-LocalDb.ps1');
  LogPath := GetEnv('TEMP') + '\nodepilot-provision.log';

  WizardForm.StatusLabel.Caption := 'Provisioning local database and services (this can take a minute)...';
  WizardForm.Refresh();

  if not Exec('powershell.exe',
       '-NoProfile -ExecutionPolicy Bypass -File "' + ProvisionScript + '" -InstallPath "' + ExpandConstant('{app}') + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    ProvisionOk := False;
    MsgBox('NodePilot could not start Windows PowerShell to set up its database and services.'#13#10#13#10
           + 'The application files are installed, but it will not run yet.'#13#10#13#10
           + 'See docs/desktop-troubleshooting.md.', mbCriticalError, MB_OK);
    Exit;
  end;

  ProvisionOk := (ResultCode = 0);
  if not ProvisionOk then
    MsgBox('NodePilot was installed, but setting up the local database and services did not finish'
           + ' (exit code ' + IntToStr(ResultCode) + ').'#13#10#13#10
           + 'The application will not start until this is resolved. The full log of this run is at:'#13#10#13#10
           + LogPath + #13#10#13#10
           + 'Troubleshooting steps: docs/desktop-troubleshooting.md', mbCriticalError, MB_OK);
end;

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

  if CurStep = ssPostInstall then
    ProvisionRuntime();
end;
