; NodePilot desktop installer (Inno Setup 6).
; Machine-wide, offline, Windows 11 x64. Built by deploy/desktop/Build-DesktopInstaller.ps1,
; which passes /DStageDir, /DAppVersion, /DOutputDir. Electron (Chromium+Node) is shipped in
; full inside StageDir\desktop; no WebView2, no runtime prerequisites, no auto-update.
;
; Command-line switches besides Inno's own:
;   setup:     /DISCARDDATA=1  delete data left by an earlier, uninstalled NodePilot (default: keep)
;   uninstall: /PURGEDATA=1    delete the database and all other data as well (default: keep)

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
; NodePilot logo on the wizard: the left banner of the welcome/finished pages and the small
; image in every inner page header. One file per scaling step, Inno picks by display DPI.
; Generated from the tracked brand assets by scripts\generate-desktop-icons.ps1.
WizardImageFile={#StageDir}\wizard-image-164x314.bmp,{#StageDir}\wizard-image-192x386.bmp,{#StageDir}\wizard-image-246x459.bmp,{#StageDir}\wizard-image-328x628.bmp
WizardSmallImageFile={#StageDir}\wizard-small-55x55.bmp,{#StageDir}\wizard-small-64x68.bmp,{#StageDir}\wizard-small-83x80.bmp,{#StageDir}\wizard-small-110x106.bmp
; Keeps a setup log in %TEMP% so a failure can be diagnosed without reproducing it.
SetupLogging=yes

[Tasks]
; The desktop shortcut is the one optional part of this install; the Start-Menu entry is created
; unconditionally. The description is literal English rather than {cm:CreateDesktopIcon}, because
; this script declares no [Languages] section.
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional shortcuts:"

[Files]
; Scripts setup runs before it copies anything (see PrepareToInstall). They come from this
; installer, not from an existing installation, whose copies belong to the older version.
; setup\ and deploy\ are separate staging trees holding the same scripts: Inno deduplicates
; identical source files, so one file listed both dontcopy and with a DestDir loses the dontcopy
; entry.
Source: "{#StageDir}\setup\*";   Flags: dontcopy
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

[UninstallDelete]
; Processed after the installed files are gone. The rendered configuration is written by the
; provisioner, not copied by setup, so Inno does not track it; Uninstall-Desktop.ps1 normally
; removes it first. The directory entries clean up what that leaves empty.
Type: files;      Name: "{app}\app\appsettings.Production.json"
Type: dirifempty; Name: "{app}\app"
Type: dirifempty; Name: "{app}"

; There is no [UninstallRun] section. Inno freezes its parameters at install time, so the
; uninstall-time choice to delete the data could never reach the script. The uninstall runs from
; CurUninstallStepChanged instead, which also reports a failed run.

[Code]
const
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7E2D2C5A-8C3F-4E9B-9D21-A1B2C3D4E5F6}_is1';

var
  ProvisionOk: Boolean;
  // Shown only when data from an earlier installation exists but NodePilot is not installed.
  DataPage: TInputOptionWizardPage;
  UninstallPurgeData: Boolean;

function DataDir(): String;
begin
  Result := ExpandConstant('{commonappdata}\NodePilot');
end;

function InstallationExists(): Boolean;
begin
  Result := RegKeyExists(HKLM64, UninstallKey) or RegKeyExists(HKLM32, UninstallKey);
end;

function DiscardData(): Boolean;
begin
  Result := (DataPage <> nil) and (DataPage.SelectedValueIndex = 1);
end;

// Guards the "Launch NodePilot" post-install entry.
function ProvisionSucceeded(): Boolean;
begin
  Result := ProvisionOk;
end;

// An existing installation is updated in place and keeps its data. Only data left behind by an
// uninstall is a genuine choice: keeping it also keeps the accounts, so the first launch shows a
// login form instead of the page that creates an administrator.
procedure InitializeWizard();
begin
  if InstallationExists() or not DirExists(DataDir()) then Exit;

  DataPage := CreateInputOptionPage(wpSelectTasks,
    'Existing NodePilot data',
    'Data from an earlier NodePilot installation is still on this computer.',
    'Location: ' + DataDir() + #13#10#13#10 +
    'It contains the database with your workflows, credentials, user accounts and execution ' +
    'history, plus keys, settings and logs.' + #13#10#13#10 +
    'Keep it to continue where you left off. Sign in with the administrator account you created ' +
    'back then; NodePilot has no default password.' + #13#10#13#10 +
    'Delete it to start with an empty database. NodePilot then asks you to create a new ' +
    'administrator when it first starts.',
    True, False);
  DataPage.Add('Keep the existing data');
  DataPage.Add('Delete the existing data and start fresh');
  if ExpandConstant('{param:DISCARDDATA|0}') <> '0' then
    DataPage.SelectedValueIndex := 1
  else
    DataPage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (DataPage <> nil) and (CurPageID = DataPage.ID) and (DataPage.SelectedValueIndex = 1) then
    Result := SuppressibleMsgBox('Delete the existing NodePilot data?' + #13#10#13#10 +
      'This permanently removes the database with all workflows, credentials, user accounts and ' +
      'execution history. It cannot be undone.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  Result := Result + 'NodePilot data:' + NewLine + Space;
  if InstallationExists() then
    Result := Result + 'The existing installation is updated. Its database, settings and user accounts are kept.'
  else if DiscardData() then
    Result := Result + 'The existing data is deleted. Create a new administrator when NodePilot first starts.'
  else if DataPage <> nil then
    Result := Result + 'The existing data is kept. Sign in with your existing administrator account.'
  else
    Result := Result + 'A new database is created. Create the administrator account when NodePilot first starts.';
end;

// Runs before any file is copied. An existing installation holds its program files open (the
// shell, both services, PostgreSQL), and replacing an open file fails, so the new installer's own
// preparation script backs up the database and stops all of it first. Returning a message aborts
// setup with a non-zero exit code.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Params: String;
begin
  Result := '';
  ExtractTemporaryFiles('*.ps1');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Prepare-DesktopSetup.ps1') +
    '" -InstallPath "' + ExpandConstant('{app}') + '"';
  if DiscardData() then
    Params := Params + ' -DiscardData';

  if not Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Setup could not start Windows PowerShell to prepare the installation.'
  else if ResultCode <> 0 then
    Result := 'Setup could not stop the NodePilot installation that is already on this computer ' +
      '(exit code ' + IntToStr(ResultCode) + '). No program files were changed.' + #13#10#13#10 +
      'The full log is at:' + #13#10 + GetEnv('TEMP') + '\nodepilot-setup-prepare.log';
end;

// Runs elevated provisioning and reports its exit code. Failure does not roll back files or a
// database that the user may still need; the installer reports the log instead.
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
    SuppressibleMsgBox('NodePilot could not start Windows PowerShell to set up its database and services.'#13#10#13#10
           + 'The application files are installed, but it will not run yet.'#13#10#13#10
           + 'See docs/desktop-troubleshooting.md.', mbCriticalError, MB_OK, IDOK);
    Exit;
  end;

  // Suppressible: a plain MsgBox is shown even under /SUPPRESSMSGBOXES, and an unattended install
  // would wait on it forever.
  ProvisionOk := (ResultCode = 0);
  if not ProvisionOk then
    SuppressibleMsgBox('NodePilot was installed, but setting up the local database and services did not finish'
           + ' (exit code ' + IntToStr(ResultCode) + ').'#13#10#13#10
           + 'The application will not start until this is resolved. The full log of this run is at:'#13#10#13#10
           + LogPath + #13#10#13#10
           + 'Troubleshooting steps: docs/desktop-troubleshooting.md', mbCriticalError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ProvisionRuntime();
end;

// A failed provisioning cannot abort setup any more, but it must not exit 0 either: an unattended
// deployment would report a working installation.
function GetCustomSetupExitCode(): Integer;
begin
  if ProvisionOk then
    Result := 0
  else
    Result := 20;
end;

// ---------------------------------------------------------------------------
// Uninstall
// ---------------------------------------------------------------------------
// The program, both services and the certificate always go. The data - the database with
// workflows, credentials and user accounts, plus keys, settings and logs - is the one question.
// Keeping it is the default everywhere, including /SILENT and a closed dialog, because an
// uninstall nobody watched must not destroy data. /PURGEDATA=1 deletes it unattended.

function InitializeUninstall(): Boolean;
var
  Response: Integer;
begin
  Result := True;
  UninstallPurgeData := ExpandConstant('{param:PURGEDATA|0}') <> '0';

  // An explicit switch is an answer; do not ask again.
  if UninstallSilent() or UninstallPurgeData then Exit;

  Response := SuppressibleTaskDialogMsgBox(
    'Keep your NodePilot data?',
    'The program, its two Windows services and its certificate are removed either way.' + #13#10#13#10 +
    'Keep data: the database with your workflows, credentials, user accounts and execution ' +
    'history stays in ' + DataDir() + ', together with keys, settings and logs. Installing ' +
    'NodePilot again continues with it - sign in with your existing account.' + #13#10#13#10 +
    'Delete everything: also removes the database and every other NodePilot file on this ' +
    'computer, including the per-user app data. This cannot be undone. The next installation ' +
    'starts empty and asks you to create a new administrator.',
    mbConfirmation, MB_YESNOCANCEL, ['Keep data', 'Delete everything', 'Cancel'], 0, IDYES);
  if Response = IDCANCEL then
    Result := False
  else
    UninstallPurgeData := Response = IDNO;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ScriptPath, Params: String;
begin
  if CurUninstallStep <> usUninstall then Exit;

  // usUninstall runs before Inno deletes the installed files, so the script is still there.
  ScriptPath := ExpandConstant('{app}\deploy\Uninstall-Desktop.ps1');
  if not FileExists(ScriptPath) then
  begin
    SuppressibleMsgBox('The NodePilot uninstall script is missing:' + #13#10 + ScriptPath + #13#10#13#10 +
      'The NodePilot services, the certificate and the data have NOT been removed.',
      mbCriticalError, MB_OK, IDOK);
    Exit;
  end;

  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -InstallPath "' +
    ExpandConstant('{app}') + '"';
  if UninstallPurgeData then
    Params := Params + ' -PurgeData';

  if not Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    SuppressibleMsgBox('Could not start Windows PowerShell to remove the NodePilot services.',
      mbCriticalError, MB_OK, IDOK)
  else if ResultCode <> 0 then
    SuppressibleMsgBox('NodePilot could not be removed completely (exit code ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
      'The full log is at:' + #13#10 + GetEnv('TEMP') + '\nodepilot-uninstall.log',
      mbError, MB_OK, IDOK);
end;
