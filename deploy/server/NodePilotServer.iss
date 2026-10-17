; NodePilot server setup (Inno Setup 6).
;
; A wizard in front of Install-NodePilot.ps1: pages and payload, no installation logic. Collected
; values go into an ACL-protected JSON answer file that deploy\Invoke-NodePilotSetup.ps1 reads and
; splats into the deployment scripts. A file rather than a command line because -PostgresPassword
; is a [SecureString], and it also enables /SILENT /ANSWERFILE= for unattended runs.
;
; There is no [Run] section: it cannot inspect an exit code, so every call goes through Exec() in
; [Code] with the result checked. Test-DeploymentTemplates.ps1 pins that.
;
; Built by deploy\server\Build-ServerInstaller.ps1, which passes the /D defines below.

#ifndef StageDir
  #define StageDir "stage"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "out"
#endif
#ifndef SignerThumbprint
  #define SignerThumbprint "0000000000000000000000000000000000000000"
#endif
#ifndef ArtifactFileName
  #define ArtifactFileName "NodePilot.zip"
#endif
#ifndef RuntimeFileName
  #define RuntimeFileName "aspnetcore-runtime-win-x64.exe"
#endif
; The .NET host. aspnetcore-runtime-*.exe carries only Microsoft.AspNetCore.App - no dotnet.exe -
; so on a machine without .NET it leaves a framework nothing can load. Installed first.
#ifndef HostRuntimeFileName
  #define HostRuntimeFileName "dotnet-runtime-win-x64.exe"
#endif

[Setup]
AppId={{03EAD540-1472-4A1B-9F06-9CB3D358E202}
AppName=NodePilot Server
AppVersion={#AppVersion}
AppPublisher=NodePilot
AppPublisherURL=https://github.com/Sev7eNup/NodePilot
DefaultDirName={autopf}\NodePilot
DefaultGroupName=NodePilot
DisableProgramGroupPage=yes
UninstallDisplayName=NodePilot Server
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; Windows Server 2022 is build 20348. The desktop installer's higher floor targets Windows 11
; and does not apply to the server installer.
MinVersion=10.0.20348
OutputDir={#OutputDir}
OutputBaseFilename=NodePilot-Server-Setup-{#AppVersion}
WizardStyle=modern
; Long prerequisite details scroll within their own pane. On smaller desktops,
; FitSetupToWorkArea also scrolls the page while keeping navigation visible.
WizardSizePercent=125,120
SetupIconFile={#StageDir}\setup-icon.ico
; NodePilot logo on the wizard: the left banner of the welcome/finished pages and the small
; image in every inner page header. One file per scaling step, Inno picks by display DPI.
; Generated from the tracked brand assets by scripts\generate-desktop-icons.ps1.
WizardImageFile={#StageDir}\wizard-image-164x314.bmp,{#StageDir}\wizard-image-192x386.bmp,{#StageDir}\wizard-image-246x459.bmp,{#StageDir}\wizard-image-328x628.bmp
WizardSmallImageFile={#StageDir}\wizard-small-55x55.bmp,{#StageDir}\wizard-small-64x68.bmp,{#StageDir}\wizard-small-83x80.bmp,{#StageDir}\wizard-small-110x106.bmp
LicenseFile={#StageDir}\LICENSE.txt
; Keeps a setup log so a failure can be diagnosed without reproducing it.
SetupLogging=yes

[Files]
; Everything setup needs at runtime is dontcopy and extracted to {tmp} on demand, because the
; phases that use it run before Inno has copied any file: the readiness page during the wizard,
; and the installation itself from PrepareToInstall.
;
; The installation cannot live in ssPostInstall: neither RaiseException nor Abort in that step
; changes the exit code, so a failed install would still report success. PrepareToInstall returns
; a message and setup exits non-zero, so failure is visible to whatever launched it.
;
; payload\ and deploy\ are separate staging trees holding the same scripts twice. Inno
; deduplicates identical source files, so listing one file both dontcopy and with a DestDir
; collapses the pair into a single entry and the dontcopy variant disappears.
Source: "{#StageDir}\payload\*.ps1"; Flags: dontcopy
Source: "{#StageDir}\payload\*.cer"; Flags: dontcopy
Source: "{#StageDir}\payload\*"; Excludes: "*.ps1,*.cer"; Flags: dontcopy solidbreak nocompression

; The only files that stay on disk: the deployment scripts, used by the uninstaller below and for
; running Update-NodePilot.ps1 by hand. Copied after PrepareToInstall, because
; Install-NodePilot.ps1 wipes its install directory before repopulating it.
Source: "{#StageDir}\deploy\*";     DestDir: "{app}\deploy"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\NodePilot Switcher"; Filename: "{app}\tools\switcher\NodePilot.Switcher.exe"; WorkingDir: "{app}\tools\switcher"

[UninstallDelete]
; Inno decides whether {app} is empty before it removes its own uninstaller from inside it, so
; the uninstall would otherwise leave an empty directory behind. This entry runs last and
; removes it.
Type: dirifempty; Name: "{app}"

; There is no [UninstallRun] section, for the same reasons [Run] is absent above.
;
; It cannot inspect an exit code, and it cannot carry an uninstall-time decision either: Inno
; evaluates {code:...} in [UninstallRun] parameters at install time and freezes the resulting
; string in unins000.dat, so a switch such as /PURGEDATA could never reach the script. The
; uninstall is invoked from [Code] instead.

[Code]
const
  ExitProbeFailed = 2;
  ExitAnswerFileInvalid = 3;
  ExitInstallFailed = 4;
  CheckCount = 10;

  // How long to wait for the adapter before giving up. Deliberately generous: it guards only
  // against an adapter that was killed, where result.ini never appears and the loop would
  // otherwise wait forever.
  AdapterTimeoutMs = 2700000;   // 45 minutes
  AdapterPollMs = 250;

  // Status glyphs, written as character codes so this file stays pure ASCII on disk and does not
  // depend on an encoding to compile.
  MarkPass = #$2713;  // check mark
  MarkFail = #$2717;  // ballot X
  MarkWarn = '!';
  MarkSkip = #$2013;  // en dash

  ColourPass = clGreen;
  ColourFail = $000000C0;
  ColourWarn = $000080C0;

var
  ContentViewport: TForm;
  ModePage: TInputOptionWizardPage;
  IdentityPage: TInputOptionWizardPage;
  AccountPage: TInputQueryWizardPage;
  ProviderPage: TInputOptionWizardPage;
  SqlPage: TInputQueryWizardPage;
  PostgresPage: TInputQueryWizardPage;
  PostgresAuthPage: TInputQueryWizardPage;
  NetworkPage: TInputQueryWizardPage;
  CertificatePage: TInputQueryWizardPage;
  ContentPage: TInputOptionWizardPage;
  ReadinessPage: TWizardPage;

  CheckIds: array[0..CheckCount - 1] of String;
  CheckTitles: array[0..CheckCount - 1] of String;
  CheckDetails: array[0..CheckCount - 1] of String;
  CheckRows: array[0..CheckCount - 1] of Integer;
  CheckList: TNewListBox;
  ReadinessSummary: TNewStaticText;
  ApplyButton: TNewButton;
  SelectedCheck: Integer;
  CheckFixes: array[0..CheckCount - 1] of TNewCheckBox;
  CheckStatus: array[0..CheckCount - 1] of String;
  // Rows cut to one line to make room; their full text is shown when the row is selected.
  // Whether a pre-ticked fix has already had its default applied. Applied once per run: a probe
  // runs again after every fix attempt, and re-applying the default would re-tick a box the
  // operator has just cleared.
  CheckFixDefaulted: array[0..CheckCount - 1] of Boolean;
  RemediationBox: TNewMemo;
  RemediationText: String;
  RecheckButton: TNewButton;
  SaveButton: TNewButton;

  // Certificate picker on the TLS page. Without it the thumbprint of an installed certificate is
  // only reachable through the certificate MMC, whose copy button prepends an invisible U+200E.
  CertCombo: TNewComboBox;
  CertThumbprints: array of String;
  CertificateSource: TNewComboBox;
  PfxBrowseButton, PemBrowseButton, PortsButton: TNewButton;
  AdvancedNetwork: TNewCheckBox;
  PostgresProvision: TNewCheckBox;
  UrlPreview: TNewStaticText;

  // Whether the bundled psql client has been extracted to {tmp}. Extraction is attempted at most
  // once per run, whether or not this build carries a client.
  PgClientExtracted: Boolean;

  // The runtime installer is a dontcopy payload too. Interactive auto-fix runs before
  // PrepareToInstall, so extraction cannot wait for that phase, and the helper is shared by the
  // silent and interactive paths, so it stays idempotent.
  RuntimePayloadExtracted: Boolean;

  // Drawn while the adapter installs, which takes minutes and would otherwise show no progress.
  ProgressPage: TOutputProgressWizardPage;
  ProgressDetailLabel: TNewStaticText;
  ProgressLogButton: TNewButton;
  AdapterTimedOut: Boolean;
  ProvisionRestartRequired: Boolean;
  AdapterRunning: Boolean;

  // Finish page. The values shown here are available nowhere else: the API key is generated by
  // the adapter, printed to a hidden console, and left out of install-report.txt by design.
  FinishMemo: TNewMemo;
  FinishSaveButton: TNewButton;
  FinishSummary: String;
  FinishHasCredentials: Boolean;
  FinishUrl: String;
  FinishOpenButton: TNewButton;

  SessionDir: String;
  AnswerFileOverride: String;
  ExistingInstallPath: String;
  ExistingDataPath: String;
  ExistingServiceName: String;
  ExistingVersion: String;
  IsUpgrade: Boolean;
  ForceFullReinstall: Boolean;
  UninstallPurgeData: Boolean;
  UninstallHandoff: Boolean;
  ProbeRan: Boolean;
  ProbeBlocking: Boolean;
  // The rows that actually hold "Next" back, collected while they are rendered. Without it the
  // blocking dialog could only say that something was wrong, on a page that may show ten rows.
  BlockingTitles: String;

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

function GetServiceName(Param: String): String;
begin
  if ExistingServiceName <> '' then
    Result := ExistingServiceName
  else
    Result := 'NodePilot';
end;

// Always the {tmp} copy, never {app}\deploy: the readiness page runs before any file has been
// installed, and one location for every phase keeps the two copies from diverging.
// {app}\deploy\ still ships, for the uninstaller and for later manual use.
function AdapterPath(): String;
begin
  Result := ExpandConstant('{tmp}\Invoke-NodePilotSetup.ps1');
end;

// -PayloadRoot is passed explicitly rather than derived from the script's own location, so the
// script and the payload can live in different directories.
function AdapterArguments(const Arguments: String): String;
begin
  Result := '-NoProfile -ExecutionPolicy Bypass -File "' + AdapterPath() + '"' +
    ' -PayloadRoot "' + ExpandConstant('{tmp}') + '" ' + Arguments;
end;

// Extracts the bundled psql client on first need rather than at startup: an installation onto
// SQL Server never touches it, and it is large enough to delay the wizard.
//
// The build script's -PgBinariesPath is optional, so a setup without a client is a valid build,
// hence try/except rather than a check. The adapter decides what to do by looking for the file.
procedure EnsurePgClient();
begin
  if PgClientExtracted then Exit;
  PgClientExtracted := True;
  // The pattern is matched against the entry's full destination path, so a bare file name finds
  // nothing; the leading wildcard is required.
  try
    ExtractTemporaryFiles('*psql.exe');
    ExtractTemporaryFiles('*.dll');
  except
    // Built without the client. The Postgres row says so on its own.
    Log('PostgreSQL client not extracted: ' + GetExceptionMessage());
  end;
end;

procedure EnsureRuntimePayload();
begin
  if RuntimePayloadExtracted then Exit;
  ExtractTemporaryFile('{#HostRuntimeFileName}');
  ExtractTemporaryFile('{#RuntimeFileName}');
  RuntimePayloadExtracted := True;
end;

function RunPowerShell(const Arguments: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec('powershell.exe', AdapterArguments(Arguments), '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
end;

// Same call, returning immediately. ResultCode says only whether the process started, because the
// process has not finished; the caller learns the outcome from the adapter's result file.
function StartPowerShell(const Arguments: String): Boolean;
var
  Ignored: Integer;
begin
  Result := Exec('powershell.exe', AdapterArguments(Arguments), '', SW_HIDE, ewNoWait, Ignored);
end;

// The adapter writes the session path with Set-Content -Encoding UTF8, which on Windows
// PowerShell 5.1 adds a byte-order mark. LoadStringFromFile returns raw bytes as an AnsiString,
// so the BOM arrives as leading characters that Trim() does not remove.
function StripBom(const Value: String): String;
begin
  Result := Value;
  if (Length(Result) >= 3) and (Ord(Result[1]) = 239) and (Ord(Result[2]) = 187) and (Ord(Result[3]) = 191) then
    Result := Copy(Result, 4, Length(Result) - 3);
  if (Length(Result) >= 1) and (Ord(Result[1]) = 65279) then
    Result := Copy(Result, 2, Length(Result) - 1);
  Result := Trim(Result);
end;

// Escapes a string for embedding in JSON. This is the only JSON handling on the Pascal side; the
// answer file is parsed strictly on the PowerShell side.
function JsonString(const Value: String): String;
var
  I: Integer;
  Ch: Char;
begin
  Result := '"';
  for I := 1 to Length(Value) do
  begin
    Ch := Value[I];
    if Ch = '"' then Result := Result + '\"'
    else if Ch = '\' then Result := Result + '\\'
    else if Ch = #8 then Result := Result + '\b'
    else if Ch = #9 then Result := Result + '\t'
    else if Ch = #10 then Result := Result + '\n'
    else if Ch = #12 then Result := Result + '\f'
    else if Ch = #13 then Result := Result + '\r'
    else if Ord(Ch) < 32 then Result := Result + '\u' + Format('%.4x', [Ord(Ch)])
    else Result := Result + Ch;
  end;
  Result := Result + '"';
end;

function JsonBool(const Value: Boolean): String;
begin
  if Value then Result := 'true' else Result := 'false';
end;

// INI values cannot contain newlines, so the adapter escapes them and this puts them back.
function ExpandNewlines(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\n', #13#10, True);
end;

// One provisioning section's verdict, as text for the operator, or '' when there is nothing to say.
// An absent section means the fix was never ticked; only a section that ran and did not pass speaks.
// Without this the runtime and signer verdicts were written by the adapter and read by nobody, so a
// fix that failed looked exactly like a fix that worked: same red row, no message.
function ProvisionSectionProblem(const Ini, Section, Caption: String): String;
var
  Status: String;
begin
  Result := '';
  Status := GetIniString(Section, 'status', '', Ini);
  if (Status = '') or (Status = 'Pass') then Exit;
  Result := Caption + ': ' + ExpandNewlines(GetIniString(Section, 'detail', '', Ini)) + #13#10#13#10;
end;

function IsLocalSystemSelected(): Boolean;
begin
  Result := IdentityPage.SelectedValueIndex = 0;
end;

function IsSqlServerSelected(): Boolean;
begin
  Result := ProviderPage.SelectedValueIndex = 0;
end;

function IsUpdateSelected(): Boolean;
begin
  Result := IsUpgrade and (not ForceFullReinstall) and (ModePage.SelectedValueIndex = 0);
end;

// ---------------------------------------------------------------------------
// Existing-installation detection
// ---------------------------------------------------------------------------

procedure DetectExistingInstallation();
begin
  IsUpgrade := False;
  ExistingServiceName := 'NodePilot';
  // The marker Install-NodePilot.ps1 writes on success. It also finds an installation that was
  // deployed from the zip by hand, which the Inno uninstall key would not.
  if RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'InstallPath', ExistingInstallPath) then
  begin
    IsUpgrade := True;
    RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'ServiceName', ExistingServiceName);
    RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'DataPath', ExistingDataPath);
    RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'Version', ExistingVersion);
  end
  else if RegQueryStringValue(HKLM64,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{03EAD540-1472-4A1B-9F06-9CB3D358E202}_is1',
    'InstallLocation', ExistingInstallPath) then
  begin
    IsUpgrade := True;
  end;
end;

// Locates the uninstaller this setup family registered. Empty when the installation came from the
// zip package, which leaves the HKLM marker DetectExistingInstallation reads but no unins000.exe.
function UninstallerPath(): String;
var
  Raw: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM64,
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{03EAD540-1472-4A1B-9F06-9CB3D358E202}_is1',
    'UninstallString', Raw) then
  begin
    Raw := RemoveQuotes(Trim(Raw));
    if (Raw <> '') and FileExists(Raw) then
    begin
      Result := Raw;
      Exit;
    end;
  end;
  if (ExistingInstallPath <> '') and FileExists(ExistingInstallPath + '\unins000.exe') then
    Result := ExistingInstallPath + '\unins000.exe';
end;

// ---------------------------------------------------------------------------
// Answer file
// ---------------------------------------------------------------------------

function AnswerFilePath(): String;
begin
  Result := SessionDir + '\answers.json';
end;

// Answer lines are built as an array rather than one concatenated string so they can be written
// with SaveStringsToUTF8File. This Inno version has no SaveStringToUTF8File, and the
// AnsiString-based SaveStringToFile would encode a non-ASCII password or host name in the system
// codepage, which the adapter, reading UTF-8, would reject or mangle.
//
// Reports whether the operator ticked a given auto-fix, looked up by check id rather than by
// array position so that inserting a check does not shift the mapping.
function IsFixRequested(const Id: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to CheckCount - 1 do
    if CheckIds[I] = Id then
    begin
      // Only the selected row shows its checkbox; all available selections remain active.
      Result := CheckFixes[I].Enabled and CheckFixes[I].Checked;
      Exit;
    end;
end;

procedure AddLine(var Lines: TArrayOfString; var Count: Integer; const Text: String);
begin
  if Count >= GetArrayLength(Lines) then
    SetArrayLength(Lines, Count + 16);
  Lines[Count] := Text;
  Count := Count + 1;
end;

function BuildAnswerLines(const AnswerMode: String; const ForProbe: Boolean): TArrayOfString;
var
  Lines: TArrayOfString;
  Count: Integer;
begin
  Count := 0;
  SetArrayLength(Lines, 40);

  if AnswerMode = 'update' then
  begin
    AddLine(Lines, Count, '{');
    AddLine(Lines, Count, '  "schemaVersion": 1,');
    AddLine(Lines, Count, '  "mode": "update",');
    AddLine(Lines, Count, '  "installPath": ' + JsonString(ExistingInstallPath) + ',');
    if ExistingDataPath <> '' then
      AddLine(Lines, Count, '  "dataPath": ' + JsonString(ExistingDataPath) + ',');
    AddLine(Lines, Count, '  "serviceName": ' + JsonString(ExistingServiceName));
    AddLine(Lines, Count, '}');
    SetArrayLength(Lines, Count);
    Result := Lines;
    Exit;
  end;

  AddLine(Lines, Count, '{');
  AddLine(Lines, Count, '  "schemaVersion": 1,');
  AddLine(Lines, Count, '  "mode": "install",');
  AddLine(Lines, Count, '  "installPath": ' + JsonString(ExpandConstant('{app}')) + ',');
  AddLine(Lines, Count, '  "dataPath": ' + JsonString(ExpandConstant('{commonappdata}\NodePilot')) + ',');
  AddLine(Lines, Count, '  "serviceName": ' + JsonString('NodePilot') + ',');
  // Absent means include on the PowerShell side, so this only ever has to say "false" clearly.
  if ContentPage.Values[0] then
    AddLine(Lines, Count, '  "includeSourceSnapshot": true,')
  else
    AddLine(Lines, Count, '  "includeSourceSnapshot": false,');
  AddLine(Lines, Count, '  "identity": {');
  if IsLocalSystemSelected() then
    AddLine(Lines, Count, '    "type": "localSystem"')
  else
  begin
    AddLine(Lines, Count, '    "type": "gmsa",');
    AddLine(Lines, Count, '    "account": ' + JsonString(Trim(AccountPage.Values[0])));
  end;
  AddLine(Lines, Count, '  },');

  AddLine(Lines, Count, '  "database": {');
  if IsSqlServerSelected() then
  begin
    AddLine(Lines, Count, '    "provider": "sqlserver",');
    AddLine(Lines, Count, '    "sqlServer": ' + JsonString(Trim(SqlPage.Values[0])) + ',');
    AddLine(Lines, Count, '    "sqlDatabase": ' + JsonString(Trim(SqlPage.Values[1])) + ',');
    AddLine(Lines, Count, '    "sqlCertificateHostName": ' + JsonString(Trim(SqlPage.Values[2])));
  end
  else
  begin
    AddLine(Lines, Count, '    "provider": "postgres",');
    AddLine(Lines, Count, '    "postgresHost": ' + JsonString(Trim(PostgresPage.Values[0])) + ',');
    AddLine(Lines, Count, '    "postgresPort": ' + Trim(PostgresPage.Values[1]) + ',');
    AddLine(Lines, Count, '    "postgresDatabase": ' + JsonString(Trim(PostgresPage.Values[2])) + ',');
    AddLine(Lines, Count, '    "postgresUser": ' + JsonString(Trim(PostgresAuthPage.Values[0])) + ',');
    AddLine(Lines, Count, '    "postgresPassword": ' + JsonString(PostgresAuthPage.Values[1]) + ',');
    AddLine(Lines, Count, '    "postgresRootCertificate": ' + JsonString(Trim(PostgresAuthPage.Values[2])));
  end;
  AddLine(Lines, Count, '  },');

  AddLine(Lines, Count, '  "network": {');
  AddLine(Lines, Count, '    "publicHostname": ' + JsonString(Trim(NetworkPage.Values[0])) + ',');
  AddLine(Lines, Count, '    "httpsPort": ' + Trim(NetworkPage.Values[1]) + ',');
  AddLine(Lines, Count, '    "httpPort": ' + Trim(NetworkPage.Values[2]) + ',');
  AddLine(Lines, Count, '    "allowedHosts": ' + JsonString(Trim(NetworkPage.Values[3])) + ',');
  AddLine(Lines, Count, '    "knownProxyIps": []');
  AddLine(Lines, Count, '  },');

  AddLine(Lines, Count, '  "certificate": {');
  if CertificateSource.ItemIndex = 1 then
  begin
    AddLine(Lines, Count, '    "thumbprint": "",');
    AddLine(Lines, Count, '    "source": "pfx",');
    AddLine(Lines, Count, '    "pfxPath": ' + JsonString(Trim(CertificatePage.Values[1])) + ',');
    AddLine(Lines, Count, '    "pfxPassword": ' + JsonString(CertificatePage.Values[2]));
  end
  else
  begin
    AddLine(Lines, Count, '    "thumbprint": ' + JsonString(Trim(CertificatePage.Values[0])) + ',');
    AddLine(Lines, Count, '    "source": "existing"');
  end;

  // The probe file carries no fix flags; it only asks. It does carry the PostgreSQL superuser,
  // because those credentials decide whether the Postgres row may offer a fix at all.
  if ForProbe and (not IsSqlServerSelected()) and PostgresProvision.Checked and (Trim(PostgresAuthPage.Values[3]) <> '') then
  begin
    AddLine(Lines, Count, '  },');
    AddLine(Lines, Count, '  "provisioning": {');
    AddLine(Lines, Count, '    "postgresSuperUser": ' + JsonString(Trim(PostgresAuthPage.Values[3])) + ',');
    AddLine(Lines, Count, '    "postgresSuperPassword": ' + JsonString(PostgresAuthPage.Values[4]));
    AddLine(Lines, Count, '  }');
  end
  else if ForProbe then
    AddLine(Lines, Count, '  }')
  else
  begin
    AddLine(Lines, Count, '  },');
    AddLine(Lines, Count, '  "provisioning": {');
    AddLine(Lines, Count, '    "installDotnetRuntime": ' + JsonBool(IsFixRequested('dotnet')) + ',');
    AddLine(Lines, Count, '    "generateSelfSignedCertificate": ' + JsonBool(IsFixRequested('certificate') and (CertificateSource.ItemIndex <> 1)) + ',');
    AddLine(Lines, Count, '    "importPfxCertificate": ' + JsonBool(IsFixRequested('certificate') and (CertificateSource.ItemIndex = 1)) + ',');
    AddLine(Lines, Count, '    "installGmsa": ' + JsonBool(IsFixRequested('gmsa')) + ',');
    AddLine(Lines, Count, '    "installAdTools": ' + JsonBool(IsFixRequested('gmsa')) + ',');
    // Two rows, one key: Provision-NodePilotDatabase.ps1 is existence-guarded throughout, so one
    // run covers both a missing database and a missing grant for the service identity. On the
    // Postgres path the same key routes to Provision-NodePilotPostgres.ps1; the provider decides
    // which script runs, so no second flag can contradict it.
    AddLine(Lines, Count, '    "createDatabaseAndLogin": ' +
      JsonBool(IsFixRequested('database') or IsFixRequested('databaseServiceLogin')) + ',');
    if (not IsSqlServerSelected()) and PostgresProvision.Checked then
    begin
      AddLine(Lines, Count, '    "postgresSuperUser": ' + JsonString(Trim(PostgresAuthPage.Values[3])) + ',');
      AddLine(Lines, Count, '    "postgresSuperPassword": ' + JsonString(PostgresAuthPage.Values[4]) + ',');
    end;
    AddLine(Lines, Count, '    "trustArtifactSigner": ' + JsonBool(IsFixRequested('signer')));
    AddLine(Lines, Count, '  }');
  end;
  AddLine(Lines, Count, '}');

  SetArrayLength(Lines, Count);
  Result := Lines;
end;

procedure WriteAnswerFile(const AnswerMode: String; const ForProbe: Boolean);
begin
  // /ANSWERFILE wins over the pages; this is the unattended path.
  //
  // The supplied file is copied into the session directory rather than used where it lies, so it
  // inherits that directory's restrictive DACL and is shredded with it. The original is untouched.
  if AnswerFileOverride <> '' then
  begin
    if not FileCopy(AnswerFileOverride, AnswerFilePath(), False) then
      RaiseException('Could not read the answer file: ' + AnswerFileOverride);
    Exit;
  end;

  // Written into the session directory, whose DACL the adapter set to SYSTEM, Administrators and
  // the installing user when it created the directory. The file inherits that, so nothing here
  // does ACL work.
  if not SaveStringsToUTF8File(AnswerFilePath(), BuildAnswerLines(AnswerMode, ForProbe), False) then
    RaiseException('Could not write the answer file to ' + AnswerFilePath());
end;

// ---------------------------------------------------------------------------
// Readiness page
// ---------------------------------------------------------------------------

function RunAdapterWithProgress(const Arguments, ResultIni, ProgressPath: String;
  var ExitCode: Integer): String; forward;

procedure UpdateFixSelection(Sender: TObject);
var
  I, Count, Row: Integer;
  Caption: String;
begin
  Count := 0;
  for I := 0 to CheckCount - 1 do
    if CheckFixes[I].Enabled and CheckFixes[I].Checked then Count := Count + 1;
  ApplyButton.Enabled := (Count > 0) and (not AdapterTimedOut) and (not ProvisionRestartRequired);
  ApplyButton.Caption := 'Apply selected fixes (' + IntToStr(Count) + ')';
  for Row := 0 to CheckList.Items.Count - 1 do
  begin
    Caption := CheckList.Items[Row];
    StringChangeEx(Caption, ' [selected]', '', True);
    I := CheckRows[Row];
    if CheckFixes[I].Enabled and CheckFixes[I].Checked then Caption := Caption + ' [selected]';
    CheckList.Items[Row] := Caption;
  end;
end;

procedure UpdateRemediation(Index: Integer);
var
  Ini, Hint, Remediation: String;
  I: Integer;
begin
  SelectedCheck := Index;
  Ini := SessionDir + '\probe.ini';
  Hint := GetIniString('check.' + CheckIds[Index], 'hint', '', Ini);
  Remediation := GetIniString('check.' + CheckIds[Index], 'remediation', '', Ini);
  RemediationText := CheckTitles[Index] + #13#10 + ExpandNewlines(CheckDetails[Index]);
  if CheckFixes[Index].Enabled then
    RemediationText := RemediationText + #13#10#13#10 + 'Available fix: ' +
      GetIniString('check.' + CheckIds[Index], 'autoFixLabel', '', Ini);
  if Hint <> '' then RemediationText := RemediationText + #13#10#13#10 + 'Next step: ' + ExpandNewlines(Hint);
  if Remediation <> '' then RemediationText := RemediationText + #13#10#13#10 + 'Manual instructions:' + #13#10 + ExpandNewlines(Remediation);
  RemediationBox.Text := RemediationText;
  for I := 0 to CheckCount - 1 do
    CheckFixes[I].Visible := (I = Index) and CheckFixes[I].Enabled;
end;

procedure CheckListClick(Sender: TObject);
begin
  if CheckList.ItemIndex >= 0 then UpdateRemediation(CheckRows[CheckList.ItemIndex]);
end;

procedure LayoutReadiness();
var
  I, Priority, Row, Failed, Notices, Fixable, Passed: Integer;
  Mark: String;
begin
  Failed := 0;
  Notices := 0;
  Fixable := 0;
  Passed := 0;
  Row := 0;
  CheckList.Items.Clear();
  for Priority := 0 to 3 do
    for I := 0 to CheckCount - 1 do
    begin
      if ((Priority = 0) and (CheckStatus[I] = 'Fail')) or
         ((Priority = 1) and (CheckStatus[I] = 'Warn')) or
         ((Priority = 2) and (CheckStatus[I] = 'Pass')) or
         ((Priority = 3) and (CheckStatus[I] = 'Skipped')) then
      begin
        Mark := '-';
        if CheckStatus[I] = 'Fail' then begin Mark := 'Needs attention'; Failed := Failed + 1; end
        else if CheckStatus[I] = 'Warn' then begin Mark := 'Notice'; Notices := Notices + 1; end
        else if CheckStatus[I] = 'Pass' then begin Mark := 'OK'; Passed := Passed + 1; end;
        if CheckFixes[I].Enabled then begin Mark := Mark + ' / fix available'; Fixable := Fixable + 1; end;
        CheckList.Items.Add('[' + Mark + '] ' + CheckTitles[I]);
        CheckRows[Row] := I;
        Row := Row + 1;
      end;
    end;
  ReadinessSummary.Caption := 'Needs attention: ' + IntToStr(Failed) + '   |   Notices: ' +
    IntToStr(Notices) + '   |   Available fixes: ' + IntToStr(Fixable) + '   |   Passed: ' + IntToStr(Passed);
  if ProvisionRestartRequired then ReadinessSummary.Caption := 'Restart this computer, then run setup again to finish preparing the gMSA.';
  if Row > 0 then
  begin
    CheckList.ItemIndex := 0;
    CheckListClick(CheckList);
  end;
  UpdateFixSelection(nil);
  WizardForm.NextButton.Enabled := ProbeRan and (not ProbeBlocking);
end;

// Created lazily, because both the readiness page and PrepareToInstall need it and either can be
// the first to run.
function EnsureSession(): String;
var
  ResultCode: Integer;
  HandoffFile: String;
  // LoadStringFromFile returns an AnsiString; this Inno version has no UTF-8 counterpart. Safe
  // because the adapter puts the session under %ProgramData%, whose path is ASCII, unlike %TEMP%,
  // which contains the account name.
  RawSession: AnsiString;
begin
  Result := '';
  if SessionDir <> '' then Exit;

  HandoffFile := ExpandConstant('{tmp}\session.txt');
  if not RunPowerShell('-Mode InitSession -HandoffPath "' + HandoffFile + '"', ResultCode) or (ResultCode <> 0) then
  begin
    Result := 'Could not create the protected setup session directory.';
    Exit;
  end;
  if not LoadStringFromFile(HandoffFile, RawSession) then
  begin
    Result := 'Could not read back the setup session directory.';
    Exit;
  end;
  SessionDir := StripBom(String(RawSession));
  DeleteFile(HandoffFile);
  if SessionDir = '' then
    Result := 'The setup session directory came back empty.';
end;

procedure ShowReadinessFailure(const Problem: String);
var
  I: Integer;
begin
  ProbeRan := False;
  ProbeBlocking := True;
  CheckList.Items.Clear();
  for I := 0 to CheckCount - 1 do
  begin
    CheckFixes[I].Visible := False;
    CheckFixes[I].Enabled := False;
    CheckFixes[I].Checked := False;
  end;
  ReadinessSummary.Caption := 'The operation did not finish. Review the error below.';
  RemediationText := Problem;
  RemediationBox.Text := Problem;
  RecheckButton.Enabled := not AdapterTimedOut;
  UpdateFixSelection(nil);
  WizardForm.NextButton.Enabled := False;
end;

procedure RunProbe();
var
  ResultCode, I: Integer;
  Ini, Status, AutoFixLabel, Problem: String;
begin
  if AdapterTimedOut or AdapterRunning then Exit;
  ProbeRan := False;
  ProbeBlocking := True;
  WizardForm.NextButton.Enabled := False;
  Problem := EnsureSession();
  if Problem <> '' then
  begin
    ShowReadinessFailure(Problem);
    MsgBox(Problem, mbCriticalError, MB_OK);
    Exit;
  end;
  if not IsSqlServerSelected() then EnsurePgClient();
  WriteAnswerFile('install', True);
  Ini := SessionDir + '\probe.ini';
  Problem := RunAdapterWithProgress('-Mode Probe -AnswerFile "' + AnswerFilePath() +
    '" -OutFile "' + Ini + '" -ProgressFile "' + SessionDir + '\progress.txt"',
    Ini, SessionDir + '\progress.txt', ResultCode);
  if (Problem <> '') or ((ResultCode <> 0) and (ResultCode <> ExitProbeFailed)) then
  begin
    if Problem = '' then Problem := ExpandNewlines(GetIniString('summary', 'error', 'The prerequisite check failed.', Ini));
    ShowReadinessFailure(Problem);
    MsgBox(Problem, mbCriticalError, MB_OK);
    Exit;
  end;
  BlockingTitles := '';
  for I := 0 to CheckCount - 1 do
  begin
    Status := GetIniString('check.' + CheckIds[I], 'status', '', Ini);
    CheckStatus[I] := Status;
    CheckTitles[I] := GetIniString('check.' + CheckIds[I], 'title', CheckIds[I], Ini);
    CheckDetails[I] := GetIniString('check.' + CheckIds[I], 'detail', '', Ini);
    AutoFixLabel := GetIniString('check.' + CheckIds[I], 'autoFixLabel', '', Ini);
    if (Status = 'Fail') and (GetIniString('check.' + CheckIds[I], 'required', '0', Ini) = '1') then
      BlockingTitles := BlockingTitles + '  - ' + CheckTitles[I] + #13#10;
    CheckFixes[I].Visible := False;
    CheckFixes[I].Enabled := ((Status = 'Fail') or (Status = 'Warn')) and
      (GetIniString('check.' + CheckIds[I], 'canAutoFix', '0', Ini) = '1') and (AutoFixLabel <> '');
    CheckFixes[I].Caption := 'Include this fix in Apply selected fixes';
    if not CheckFixes[I].Enabled then CheckFixes[I].Checked := False
    else if (not CheckFixDefaulted[I]) and
      (GetIniString('check.' + CheckIds[I], 'autoFixDefault', '0', Ini) = '1') then
    begin
      CheckFixes[I].Checked := True;
      CheckFixDefaulted[I] := True;
    end;
  end;
  ProbeRan := True;
  ProbeBlocking := (ResultCode = ExitProbeFailed) or ProvisionRestartRequired;
  LayoutReadiness();
end;

procedure RecheckClick(Sender: TObject);
begin
  if not AdapterTimedOut then RunProbe();
end;

// SaveStringsToUTF8File wants one array entry per line, and the sources here are single strings.
function SaveTextToFile(const Target, Text: String): Boolean;
var
  Rest: String;
  Lines: TArrayOfString;
  Count, P: Integer;
begin
  Rest := Text;
  Count := 0;
  SetArrayLength(Lines, 0);
  P := Pos(#13#10, Rest);
  while P > 0 do
  begin
    SetArrayLength(Lines, Count + 1);
    Lines[Count] := Copy(Rest, 1, P - 1);
    Count := Count + 1;
    Rest := Copy(Rest, P + 2, Length(Rest));
    P := Pos(#13#10, Rest);
  end;
  SetArrayLength(Lines, Count + 1);
  Lines[Count] := Rest;
  Result := SaveStringsToUTF8File(Target, Lines, False);
end;

procedure SaveRemediationClick(Sender: TObject);
var
  Target: String;
begin
  // Inno Setup's Pascal Script has no clipboard API, so writing the text to a file is how the
  // instructions leave the wizard.
  Target := ExpandConstant('{userdesktop}\nodepilot-prerequisites.txt');
  if SaveTextToFile(Target, RemediationText) then
    MsgBox('Saved to ' + Target, mbInformation, MB_OK)
  else
    MsgBox('Could not write ' + Target, mbError, MB_OK);
end;

procedure FinishSaveClick(Sender: TObject);
var
  Target, MessageText: String;
begin
  if IsUpdateSelected() then
    Target := ExpandConstant('{userdesktop}\nodepilot-update-summary.txt')
  else
    Target := ExpandConstant('{userdesktop}\nodepilot-setup-summary.txt');
  if SaveTextToFile(Target, FinishSummary) then
  begin
    MessageText := 'Saved to ' + Target;
    if FinishHasCredentials then
      MessageText := MessageText + #13#10#13#10 +
        'It contains your setup credentials. Move it somewhere safe and delete it from ' +
        'the desktop once you are done.';
    MsgBox(MessageText, mbInformation, MB_OK);
  end
  else
    MsgBox('Could not write ' + Target, mbError, MB_OK);
end;

// Everything needed to reach the installation for the first time, assembled from the adapter's
// result file. The External-Trigger API key is absent from install-report.txt and the admin setup
// token is consumed by the first login, so neither is available anywhere else.
procedure BuildFinishSummary(const ResultIni: String);
var
  Url, Token, ApiKey, Thumb, InstallDir, DataDir, Service, S: String;
  BootstrapUser, BootstrapPassword: String;
begin
  Url := GetIniString('result', 'url', '', ResultIni);
  FinishUrl := Url;
  Token := GetIniString('result', 'adminSetupToken', '', ResultIni);
  ApiKey := GetIniString('result', 'externalTriggerApiKey', '', ResultIni);
  Thumb := GetIniString('result', 'certificateThumbprint', '', ResultIni);
  InstallDir := GetIniString('result', 'installPath', '', ResultIni);
  DataDir := GetIniString('result', 'dataPath', '', ResultIni);
  Service := GetIniString('result', 'serviceName', '', ResultIni);

  BootstrapUser := GetIniString('bootstrap', 'username', '', ResultIni);
  BootstrapPassword := GetIniString('bootstrap', 'password', '', ResultIni);
  FinishHasCredentials := (Token <> '') or (ApiKey <> '') or (BootstrapPassword <> '');

  // A label per line with the value straight after it. The memo has no word wrap, so long prose
  // lines would be cut off at the right edge.
  S := '';
  if Url <> '' then S := S + 'Address       ' + Url + #13#10;

  // First access precedes the installation details.
  S := S + #13#10;
  if BootstrapUser <> '' then
  begin
    S := S + 'Sign in with' + #13#10;
    S := S + '  User        ' + BootstrapUser + #13#10;
    S := S + '  Password    ' + BootstrapPassword + #13#10;
  end
  else if Token <> '' then
  begin
    S := S + 'Setup token (first login only)' + #13#10;
    S := S + '  ' + Token + #13#10;
  end
  else if GetIniString('result', 'adminSetupTokenUnreadable', '', ResultIni) <> '' then
  begin
    S := S + 'Setup token unreadable. Retrieve it with:' + #13#10;
    S := S + '  robocopy "' + DataDir + '" "%TEMP%" admin-setup.token /B' + #13#10;
  end
  else if not IsUpdateSelected() then
    S := S + 'Sign in with an existing account - no setup token was issued.' + #13#10;

  if ApiKey <> '' then
  begin
    S := S + #13#10 + 'API key (header X-Api-Key), shown only here' + #13#10;
    S := S + '  ' + ApiKey + #13#10;
  end;

  S := S + #13#10 + 'Installation details' + #13#10;
  if Service <> '' then S := S + 'Service       ' + Service + #13#10;
  if InstallDir <> '' then S := S + 'Program       ' + InstallDir + #13#10;
  if DataDir <> '' then S := S + 'Data          ' + DataDir + #13#10;
  if Thumb <> '' then S := S + 'Certificate   ' + Thumb + #13#10;
  FinishSummary := S;
end;

// ---------------------------------------------------------------------------
// Certificate picker
// ---------------------------------------------------------------------------

// Pops the text up to the next '|' off Rest and returns it. Inno's Pascal has no split, and
// Format-NodePilotCertificateLine fixes the line at four fields with no separator in the subject.
function PopField(var Rest: String): String;
var
  Separator: Integer;
begin
  Separator := Pos('|', Rest);
  if Separator = 0 then
  begin
    Result := Rest;
    Rest := '';
  end
  else
  begin
    Result := Copy(Rest, 1, Separator - 1);
    Rest := Copy(Rest, Separator + 1, Length(Rest) - Separator);
  end;
end;

// Inno lays an input page out at a fixed height per label+edit pair, which is more than the
// controls need. Five pairs fill nearly the whole surface and push the certificate picker below
// the bottom edge, and an input page does not scroll. Reflowing the controls at their real heights
// frees enough room for the picker on the same page. Positions are measured off the controls
// rather than off constants, so a font change or another field moves them.
procedure NetworkChanged(Sender: TObject);
begin
  UrlPreview.Caption := 'Address: https://' + Trim(NetworkPage.Values[0]) + ':' + Trim(NetworkPage.Values[1]) + '/';
  NetworkPage.PromptLabels[3].Visible := AdvancedNetwork.Checked;
  NetworkPage.Edits[3].Visible := AdvancedNetwork.Checked;
end;

procedure CertificateSourceChanged(Sender: TObject);
begin
  CertificatePage.PromptLabels[0].Visible := CertificateSource.ItemIndex = 0;
  CertificatePage.Edits[0].Visible := CertificateSource.ItemIndex = 0;
  CertCombo.Visible := CertificateSource.ItemIndex = 0;
  CertificatePage.PromptLabels[1].Visible := CertificateSource.ItemIndex = 1;
  CertificatePage.Edits[1].Visible := CertificateSource.ItemIndex = 1;
  CertificatePage.PromptLabels[2].Visible := CertificateSource.ItemIndex = 1;
  CertificatePage.Edits[2].Visible := CertificateSource.ItemIndex = 1;
  PfxBrowseButton.Visible := CertificateSource.ItemIndex = 1;
  if CertificateSource.ItemIndex <> 0 then CertificatePage.Values[0] := '';
  if CertificateSource.ItemIndex <> 1 then CertificatePage.Values[2] := '';
end;

procedure BrowseCertificate(Sender: TObject);
var
  FileName: String;
begin
  if Sender = PfxBrowseButton then
  begin
    FileName := CertificatePage.Values[1];
    if GetOpenFileName('Select the HTTPS certificate', FileName, '', 'PFX files|*.pfx;*.p12|All files|*.*', 'pfx') then
      CertificatePage.Values[1] := FileName;
  end
  else
  begin
    FileName := PostgresAuthPage.Values[2];
    if GetOpenFileName('Select the PostgreSQL root CA', FileName, '', 'Certificate files|*.pem;*.crt;*.cer|All files|*.*', 'pem') then
      PostgresAuthPage.Values[2] := FileName;
  end;
end;

procedure PostgresProvisionChanged(Sender: TObject);
var
  I: Integer;
begin
  for I := 3 to 4 do
  begin
    PostgresAuthPage.PromptLabels[I].Visible := PostgresProvision.Checked;
    PostgresAuthPage.Edits[I].Visible := PostgresProvision.Checked;
  end;
end;

procedure SuggestPortsClick(Sender: TObject);
var
  Problem, Ini, Port: String;
  Code: Integer;
begin
  Problem := EnsureSession();
  if Problem <> '' then begin MsgBox(Problem, mbError, MB_OK); Exit; end;
  Ini := SessionDir + '\ports.ini';
  Problem := RunAdapterWithProgress('-Mode Ports -ProbeServiceName "' + GetServiceName('') +
    '" -OutFile "' + Ini + '" -ProgressFile "' + SessionDir + '\progress.txt"',
    Ini, SessionDir + '\progress.txt', Code);
  if (Problem <> '') or (Code <> 0) then
  begin
    if Problem = '' then Problem := GetIniString('summary', 'error', 'Could not check ports.', Ini);
    MsgBox(Problem, mbError, MB_OK);
    Exit;
  end;
  Port := GetIniString('ports', 'https', '', Ini);
  if Port = '' then begin MsgBox('Ports 443, 8443 and 9443 are unavailable. Choose another HTTPS port.', mbInformation, MB_OK); Exit; end;
  if MsgBox('Use HTTPS port ' + Port + ' and disable the HTTP redirect? HTTPS remains available directly.',
    mbConfirmation, MB_YESNO) = IDYES then
  begin
    NetworkPage.Values[1] := Port;
    NetworkPage.Values[2] := '0';
    NetworkChanged(nil);
  end;
end;

procedure OpenSetupLog(Sender: TObject);
var
  Code: Integer;
begin
  ShellExec('open', 'notepad.exe', '"' + ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log"', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure OpenInstalledSite(Sender: TObject);
var
  Code: Integer;
begin
  if FinishUrl <> '' then ShellExecAsOriginalUser('open', FinishUrl, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure CertComboChange(Sender: TObject);
begin
  // Entry 0 is the prompt and maps to an empty thumbprint, so selecting it does not wipe a
  // thumbprint typed by hand.
  if (CertCombo.ItemIndex > 0) and (CertCombo.ItemIndex < GetArrayLength(CertThumbprints)) then
    CertificatePage.Values[0] := CertThumbprints[CertCombo.ItemIndex];
end;

procedure LoadCertificateList();
var
  ResultCode, Count, Added, I: Integer;
  Ini, Rest, Thumbprint, Subject, HasKey, Expires, Entry, Current: String;
begin
  // Into {tmp} rather than the protected session directory: this runs on the TLS page, before a
  // session exists, and thumbprints and subjects from a world-readable store are not secret.
  Ini := ExpandConstant('{tmp}\certificates.ini');
  DeleteFile(Ini);
  CertCombo.Items.Clear();
  SetArrayLength(CertThumbprints, 1);
  CertThumbprints[0] := '';
  WizardForm.Update();

  if not RunPowerShell('-Mode Certificates -OutFile "' + Ini + '"', ResultCode) or (ResultCode <> 0) then
  begin
    // Never blocking: the field above still takes a thumbprint typed by hand and the prerequisite
    // page checks it either way, so a picker that cannot be filled only costs convenience.
    CertCombo.Items.Add('The certificate list could not be read - type the thumbprint above');
    CertCombo.ItemIndex := 0;
    Exit;
  end;

  Count := StrToIntDef(GetIniString('certificates', 'count', '0', Ini), 0);
  if Count = 0 then
    CertCombo.Items.Add('No certificates on this computer - import the PFX into the machine store')
  else
    CertCombo.Items.Add('Certificates on this computer - pick one to fill the box above');

  SetArrayLength(CertThumbprints, Count + 1);
  Added := 1;
  for I := 0 to Count - 1 do
  begin
    Rest := GetIniString('certificates', IntToStr(I), '', Ini);
    if Rest = '' then Continue;
    Thumbprint := PopField(Rest);
    Subject := PopField(Rest);
    HasKey := PopField(Rest);
    Expires := Rest;
    // A malformed line is dropped rather than offered: selecting it would put a non-thumbprint
    // into the field and fail the next page for the wrong reason.
    if Length(Thumbprint) <> 40 then Continue;

    // The thumbprint is part of the caption. Two certificates can share a subject and an expiry
    // date, which would render as identical lines, and showing the value that lands in the field
    // above lets the operator check it against a thumbprint they were given.
    Entry := Subject + '   ' + Thumbprint + '   expires ' + Expires;
    // Listed rather than filtered out, so a certificate imported without its private key is
    // visible together with the reason it cannot be used.
    if HasKey <> '1' then
      Entry := Entry + '   NO PRIVATE KEY';
    CertCombo.Items.Add(Entry);
    CertThumbprints[Added] := Thumbprint;
    Added := Added + 1;
  end;
  SetArrayLength(CertThumbprints, Added);

  // Preselect what the field already holds, including a thumbprint written back by a generated
  // certificate, so returning to this page does not show the prompt over a value that is set.
  CertCombo.ItemIndex := 0;
  Current := Trim(CertificatePage.Values[0]);
  if Current <> '' then
    for I := 1 to Added - 1 do
      if CompareText(CertThumbprints[I], Current) = 0 then
        CertCombo.ItemIndex := I;
end;

procedure ApplyFixesClick(Sender: TObject);
var
  I, ResultCode: Integer;
  WantsFix: Boolean;
  ProvisionIni, DbStatus, Problems, SetupError, Thumbprint: String;
begin
    if AdapterTimedOut or AdapterRunning or ProvisionRestartRequired then Exit;
    WantsFix := False;
    for I := 0 to CheckCount - 1 do
      if CheckFixes[I].Enabled and CheckFixes[I].Checked then
        WantsFix := True;

    if WantsFix then
    begin
      if not IsSqlServerSelected() then EnsurePgClient();
      EnsureRuntimePayload();
      WriteAnswerFile('install', False);
      ProvisionIni := SessionDir + '\provision.ini';
      SetupError := RunAdapterWithProgress('-Mode Provision -AnswerFile "' + AnswerFilePath() +
        '" -OutFile "' + ProvisionIni + '" -ProgressFile "' + SessionDir + '\progress.txt"',
        ProvisionIni, SessionDir + '\progress.txt', ResultCode);
      if (SetupError <> '') or (ResultCode <> 0) then
      begin
        // Show the adapter's reason and clear the attempted selections below, even after failure.
        if SetupError = '' then SetupError := ExpandNewlines(GetIniString('summary', 'error', '', ProvisionIni));
        if SetupError <> '' then SetupError := SetupError + #13#10#13#10;
        MsgBox('The requested changes could not be applied.' + #13#10#13#10 + SetupError +
               'See ' + ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log.', mbCriticalError, MB_OK);
      end
      else
      begin
        // Read back, not assumed. The adapter reports every fix it ran, and a verdict nobody reads
        // is a fix that silently did nothing.
        Problems := ProvisionSectionProblem(ProvisionIni, 'provision.runtime', 'The ASP.NET Core runtime') +
          ProvisionSectionProblem(ProvisionIni, 'provision.signer', 'The publisher certificate') +
          ProvisionSectionProblem(ProvisionIni, 'provision.gmsa', 'The service account');

        // Read each action's result: a successful worker exit can still contain a failed fix.
        DbStatus := GetIniString('provision.database', 'status', '', ProvisionIni);
        if (DbStatus <> '') and (DbStatus <> 'Pass') then
          // Kept on one continuation line: a line that STARTS with '#' is read by the preprocessor as
          // a directive, so '#13#10' may never begin one.
          MsgBox('The database could not be prepared:' + #13#10#13#10 +
                 ExpandNewlines(GetIniString('provision.database', 'detail', '', ProvisionIni)) + #13#10#13#10 +
                 'Select the database line below for the statements to hand to a DBA.', mbError, MB_OK)
        else if Problems <> '' then
          MsgBox('Not everything could be applied:' + #13#10#13#10 + Problems, mbError, MB_OK)
        else if StrToIntDef(GetIniString('summary', 'actionsPerformed', '0', ProvisionIni), 0) = 0 then
          MsgBox('Nothing was changed - none of the ticked items produced an action.' + #13#10#13#10 +
                 'See ' + ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log.', mbError, MB_OK);
      end;

      // A generated certificate produces a thumbprint the operator never typed.
      Thumbprint := GetIniString('provision.certificate', 'thumbprint', '', ProvisionIni);
      ProvisionRestartRequired := GetIniString('provision.gmsa', 'restartRequired', '0', ProvisionIni) = '1';
      if Thumbprint <> '' then
      begin
        CertificatePage.Values[0] := Thumbprint;
        CertificateSource.ItemIndex := 0;
        CertificatePage.Values[2] := '';
        CertificatePage.Values[1] := '';
      end;

      // A retry requires a fresh selection; the probe determines which fixes remain available.
      for I := 0 to CheckCount - 1 do
        CheckFixes[I].Checked := False;

      // Never assume a fix worked.
      if not AdapterTimedOut then RunProbe()
      else ShowReadinessFailure(SetupError);
      Exit;
    end;

end;

procedure CreateReadinessPage();
var
  I: Integer;
begin
  ReadinessPage := CreateCustomPage(ContentPage.ID, 'Prerequisites',
    'Review the checks, apply selected fixes, then continue.');
  ReadinessSummary := TNewStaticText.Create(ReadinessPage);
  ReadinessSummary.Parent := ReadinessPage.Surface;
  ReadinessSummary.Width := ReadinessPage.SurfaceWidth;
  ReadinessSummary.Height := ScaleY(24);
  ReadinessSummary.Font.Style := [fsBold];

  CheckList := TNewListBox.Create(ReadinessPage);
  CheckList.Parent := ReadinessPage.Surface;
  CheckList.Top := ScaleY(28);
  CheckList.Width := ReadinessPage.SurfaceWidth;
  CheckList.Height := ReadinessPage.SurfaceHeight div 2 - ScaleY(28);
  CheckList.OnClick := @CheckListClick;

  for I := 0 to CheckCount - 1 do
  begin
    CheckFixes[I] := TNewCheckBox.Create(ReadinessPage);
    CheckFixes[I].Parent := ReadinessPage.Surface;
    CheckFixes[I].Top := CheckList.Top + CheckList.Height + ScaleY(8);
    CheckFixes[I].Width := ReadinessPage.SurfaceWidth;
    CheckFixes[I].Height := ScaleY(32);
    CheckFixes[I].Visible := False;
    CheckFixes[I].Enabled := False;
    CheckFixes[I].OnClick := @UpdateFixSelection;
  end;

  RemediationBox := TNewMemo.Create(ReadinessPage);
  RemediationBox.Parent := ReadinessPage.Surface;
  RemediationBox.Top := CheckList.Top + CheckList.Height + ScaleY(44);
  RemediationBox.Width := ReadinessPage.SurfaceWidth;
  RemediationBox.Height := ReadinessPage.SurfaceHeight - RemediationBox.Top - ScaleY(36);
  RemediationBox.ReadOnly := True;
  RemediationBox.WordWrap := True;
  RemediationBox.ScrollBars := ssVertical;

  ApplyButton := TNewButton.Create(ReadinessPage);
  ApplyButton.Parent := ReadinessPage.Surface;
  ApplyButton.Top := ReadinessPage.SurfaceHeight - ScaleY(25);
  ApplyButton.Width := ScaleX(170);
  ApplyButton.Height := ScaleY(23);
  ApplyButton.Caption := 'Apply selected fixes';
  ApplyButton.OnClick := @ApplyFixesClick;

  RecheckButton := TNewButton.Create(ReadinessPage);
  RecheckButton.Parent := ReadinessPage.Surface;
  RecheckButton.Left := ScaleX(178);
  RecheckButton.Top := ApplyButton.Top;
  RecheckButton.Width := ScaleX(100);
  RecheckButton.Height := ScaleY(23);
  RecheckButton.Caption := 'Check again';
  RecheckButton.OnClick := @RecheckClick;

  SaveButton := TNewButton.Create(ReadinessPage);
  SaveButton.Parent := ReadinessPage.Surface;
  SaveButton.Top := ApplyButton.Top;
  SaveButton.Left := ScaleX(286);
  SaveButton.Width := ScaleX(120);
  SaveButton.Height := ScaleY(23);
  SaveButton.Caption := 'Save instructions...';
  SaveButton.OnClick := @SaveRemediationClick;
end;

// ---------------------------------------------------------------------------
// Wizard construction
// ---------------------------------------------------------------------------

type
  TSetupMonitorInfo = record
    Size: Cardinal;
    Monitor, Work: TRect;
    Flags: Cardinal;
  end;

function MonitorFromWindow(Window: HWND; Flags: Cardinal): THandle;
  external 'MonitorFromWindow@user32.dll stdcall';
function GetMonitorInfo(Monitor: THandle; var Info: TSetupMonitorInfo): Boolean;
  external 'GetMonitorInfoW@user32.dll stdcall';
function GetSystemMetrics(Index: Integer): Integer;
  external 'GetSystemMetrics@user32.dll stdcall';

function FitSetupToWorkArea(Form: TSetupForm; Notebook: TNewNotebook): TForm;
var
  Info: TSetupMonitorInfo;
  AvailableHeight, AvailableWidth, Overflow, ContentWidth, ContentHeight, NewWidth: Integer;
begin
  Result := nil;
  Info.Size := SizeOf(Info);
  if not GetMonitorInfo(MonitorFromWindow(Form.Handle, 2), Info) then Exit;
  AvailableHeight := Info.Work.Bottom - Info.Work.Top;
  AvailableWidth := Info.Work.Right - Info.Work.Left;
  if (Form.Height <= AvailableHeight) and (Form.Width <= AvailableWidth) then Exit;

  // Preserve DPI-sized controls and scroll the page instead of clipping its fields.
  Overflow := Form.Height - AvailableHeight;
  if Overflow < 0 then Overflow := 0;
  ContentWidth := Notebook.Width;
  ContentHeight := Notebook.Height;
  Notebook.Anchors := [akLeft, akTop];
  Result := TForm.CreateNew(Form, 0);
  Result.BorderStyle := bsNone;
  Result.Position := poDesigned;
  Result.Font.Assign(Form.Font);
  Result.Parent := Form;
  Result.AutoScroll := True;
  Notebook.Parent := Result;
  Notebook.SetBounds(0, 0, ContentWidth, ContentHeight);
  NewWidth := Form.Width + GetSystemMetrics(2);
  if NewWidth > AvailableWidth then NewWidth := AvailableWidth;
  Form.Constraints.MinWidth := 0;
  Form.Constraints.MinHeight := 0;
  Form.Width := NewWidth;
  Form.Height := Form.Height - Overflow;
  Result.SetBounds(0, 0, Form.ClientWidth, ContentHeight - Overflow);
  Form.Top := Info.Work.Top;
  Form.Left := Info.Work.Left + (AvailableWidth - Form.Width) div 2;
  Result.Show;
end;

procedure InitializeWizard();
var
  I: Integer;
begin
  CheckIds[0] := 'dotnet';
  CheckIds[1] := 'certificate';
  CheckIds[2] := 'ports';
  CheckIds[3] := 'gmsa';
  CheckIds[4] := 'serviceIdentity';
  CheckIds[5] := 'domainJoined';
  CheckIds[6] := 'database';
  CheckIds[7] := 'databaseVersion';
  CheckIds[8] := 'databaseServiceLogin';
  // Last in the list, but the first thing the installation verifies: on a host that does not
  // trust the publisher, CheckSignature fails and the installation rolls back.
  CheckIds[9] := 'signer';

  // Before anything can call the adapter, and that is every phase of this wizard.
  ExtractTemporaryFiles('*.ps1');
  // The readiness page reports whether this machine trusts the publisher, so the certificate is
  // extracted here, before any phase that reads it.
  ExtractTemporaryFile('nodepilot-release-signing.cer');

  DetectExistingInstallation();
  ForceFullReinstall := ExpandConstant('{param:FULLREINSTALL|0}') <> '0';
  AnswerFileOverride := ExpandConstant('{param:ANSWERFILE|}');
  if (AnswerFileOverride <> '') and (not FileExists(AnswerFileOverride)) then
    RaiseException('The answer file given with /ANSWERFILE was not found: ' + AnswerFileOverride);

  ModePage := CreateInputOptionPage(wpWelcome,
    'Existing installation found',
    'NodePilot ' + ExistingVersion + ' is already installed in ' + ExistingInstallPath + '.',
    'What should this setup do?', True, False);
  ModePage.Add('Update the program files and keep the current configuration');
  ModePage.Add('Set the installation up again from scratch (issues a new External-Trigger API key)');
  // Removal is offered here as well as in Apps & Features, so it is reachable from the setup an
  // operator has just launched.
  ModePage.Add('Remove NodePilot from this computer (your database is left untouched)');
  ModePage.SelectedValueIndex := 0;
  // Avoid scrolling an otherwise empty list area into view when it receives focus.
  ModePage.CheckListBox.Height := ScaleY(80);

  IdentityPage := CreateInputOptionPage(wpSelectDir,
    'Service identity',
    'Which account should the NodePilot service run as?',
    'The account also authenticates to SQL Server and to WinRM targets.', True, False);
  IdentityPage.Add('LocalSystem - authenticates on the network as this computer''s account');
  IdentityPage.Add('Group managed service account (gMSA)');
  IdentityPage.SelectedValueIndex := 0;
  IdentityPage.CheckListBox.Height := ScaleY(56);

  AccountPage := CreateInputQueryPage(IdentityPage.ID,
    'Group managed service account',
    'Which gMSA should the service use?',
    'Use an existing AD account. The prerequisites page can prepare it locally, including AD tools when needed.');
  AccountPage.Add('Account (DOMAIN\name$):', False);

  ProviderPage := CreateInputOptionPage(AccountPage.ID,
    'Database',
    'Which database will NodePilot use?',
    'Choose an existing database server. Setup can create the NodePilot database and login with administrative access.', True, False);
  ProviderPage.Add('Microsoft SQL Server 2022 CU1 or newer');
  ProviderPage.Add('PostgreSQL 16 or newer');
  ProviderPage.SelectedValueIndex := 0;
  ProviderPage.CheckListBox.Height := ScaleY(56);

  // Each page is anchored to the one created before it, never to a shared parent. Inno inserts a
  // page directly after the ID it is given, so two pages anchored to the same parent come out in
  // reverse creation order, and a later page anchored to the first of them lands ahead of the
  // second.
  SqlPage := CreateInputQueryPage(ProviderPage.ID,
    'SQL Server',
    'Where does NodePilot find its database?',
    'The connection is encrypted and the server certificate is verified, so the name below must match that certificate.');
  SqlPage.Add('Server (host, host\instance or host,port):', False);
  SqlPage.Add('Database:', False);
  SqlPage.Add('Certificate host name (leave blank to derive it):', False);
  SqlPage.Values[1] := 'NodePilot';

  // Split across two pages because an Inno input page fits at most five label+edit pairs: a sixth
  // is laid out below the surface and never drawn, and the page does not scroll, so the missing
  // field gives no hint of itself. Every page here stays at three fields.
  PostgresPage := CreateInputQueryPage(SqlPage.ID,
    'PostgreSQL - server',
    'Where does NodePilot find its database?',
    'Credentials follow on the next page.');
  PostgresPage.Add('Host:', False);
  PostgresPage.Add('Port:', False);
  PostgresPage.Add('Database:', False);
  PostgresPage.Values[1] := '5432';
  PostgresPage.Values[2] := 'nodepilot';

  PostgresAuthPage := CreateInputQueryPage(PostgresPage.ID,
    'PostgreSQL - credentials',
    'How does NodePilot authenticate?',
    'The connection uses SSL Mode=VerifyFull, so a root certificate is required.');
  PostgresAuthPage.Add('User:', False);
  PostgresAuthPage.Add('Password:', True);
  PostgresAuthPage.Add('Root certificate (PEM file):', False);
  // Provisioning only, and only if the operator wants it - the service never authenticates with
  // these. SQL Server needs no equivalent: there Trusted_Connection means the installing admin's
  // own Windows identity IS the permission to create a login and a database. PostgreSQL has
  // nothing like it, so creating the role has to be asked for with credentials that can.
  PostgresAuthPage.Add('Superuser for creating the role (optional):', False);
  PostgresAuthPage.Add('Superuser password:', True);

  // Reserve space for the optional administrator fields at high DPI.
  for I := 1 to 2 do
  begin
    PostgresAuthPage.PromptLabels[I].Top := PostgresAuthPage.PromptLabels[I].Top - ScaleY(I * 6);
    PostgresAuthPage.Edits[I].Top := PostgresAuthPage.Edits[I].Top - ScaleY(I * 6);
  end;

  PostgresProvision := TNewCheckBox.Create(PostgresAuthPage);
  PostgresProvision.Parent := PostgresAuthPage.Surface;
  PostgresProvision.Top := PostgresAuthPage.Edits[2].Top + PostgresAuthPage.Edits[2].Height + ScaleY(10);
  PostgresProvision.Width := PostgresAuthPage.SurfaceWidth;
  PostgresProvision.Height := ScaleY(20);
  PostgresProvision.Caption := 'Prepare the database and login using an administrator account';
  PostgresProvision.OnClick := @PostgresProvisionChanged;
  for I := 3 to 4 do
  begin
    PostgresAuthPage.PromptLabels[I].Top := PostgresProvision.Top + ScaleY(24) + ScaleY((I - 3) * 46);
    PostgresAuthPage.Edits[I].Top := PostgresAuthPage.PromptLabels[I].Top + PostgresAuthPage.PromptLabels[I].Height + ScaleY(4);
  end;
  PostgresProvisionChanged(nil);
  PemBrowseButton := TNewButton.Create(PostgresAuthPage);
  PemBrowseButton.Parent := PostgresAuthPage.Surface;
  PemBrowseButton.Top := PostgresAuthPage.Edits[2].Top;
  PemBrowseButton.Left := PostgresAuthPage.SurfaceWidth - ScaleX(78);
  PemBrowseButton.Width := ScaleX(78);
  PemBrowseButton.Height := PostgresAuthPage.Edits[2].Height;
  PemBrowseButton.Caption := 'Browse...';
  PemBrowseButton.OnClick := @BrowseCertificate;
  PostgresAuthPage.Edits[2].Width := PemBrowseButton.Left - ScaleX(8);

  NetworkPage := CreateInputQueryPage(PostgresAuthPage.ID,
    'Network', 'How will clients reach NodePilot?',
    'Choose the address clients will use. The HTTPS certificate follows on the next page.');
  NetworkPage.Add('Public host name:', False);
  NetworkPage.Add('HTTPS port:', False);
  NetworkPage.Add('HTTP port (0 disables the redirect):', False);
  NetworkPage.Add('Allowed host names (semicolon separated; blank uses the public host):', False);
  NetworkPage.Values[1] := '443';
  NetworkPage.Values[2] := '80';
  for I := 1 to 2 do
  begin
    NetworkPage.PromptLabels[I].Top := NetworkPage.PromptLabels[I].Top - ScaleY(I * 6);
    NetworkPage.Edits[I].Top := NetworkPage.Edits[I].Top - ScaleY(I * 6);
  end;
  AdvancedNetwork := TNewCheckBox.Create(NetworkPage);
  AdvancedNetwork.Parent := NetworkPage.Surface;
  AdvancedNetwork.Top := NetworkPage.Edits[2].Top + NetworkPage.Edits[2].Height + ScaleY(12);
  AdvancedNetwork.Caption := 'Advanced host settings';
  AdvancedNetwork.Width := NetworkPage.SurfaceWidth;
  AdvancedNetwork.Height := ScaleY(20);
  AdvancedNetwork.OnClick := @NetworkChanged;
  NetworkPage.PromptLabels[3].Top := AdvancedNetwork.Top + ScaleY(26);
  NetworkPage.Edits[3].Top := NetworkPage.PromptLabels[3].Top + NetworkPage.PromptLabels[3].Height + ScaleY(4);
  UrlPreview := TNewStaticText.Create(NetworkPage);
  UrlPreview.Parent := NetworkPage.Surface;
  UrlPreview.Top := NetworkPage.SurfaceHeight - ScaleY(48);
  UrlPreview.Width := NetworkPage.SurfaceWidth;
  UrlPreview.Height := ScaleY(25);
  PortsButton := TNewButton.Create(NetworkPage);
  PortsButton.Parent := NetworkPage.Surface;
  PortsButton.Top := NetworkPage.SurfaceHeight - ScaleY(28);
  PortsButton.Width := ScaleX(190);
  PortsButton.Height := ScaleY(23);
  PortsButton.Caption := 'Find available ports...';
  PortsButton.OnClick := @SuggestPortsClick;
  NetworkPage.Edits[0].OnChange := @NetworkChanged;
  NetworkPage.Edits[1].OnChange := @NetworkChanged;
  NetworkChanged(nil);

  CertificatePage := CreateInputQueryPage(NetworkPage.ID, 'HTTPS certificate',
    'Choose how to provide the server certificate', '');
  CertificatePage.Add('Certificate thumbprint:', False);
  CertificatePage.Add('PFX file:', False);
  CertificatePage.Add('PFX password (leave blank if unprotected):', True);
  for I := 0 to 2 do
  begin
    CertificatePage.PromptLabels[I].Top := ScaleY(48 + I * 58);
    CertificatePage.Edits[I].Top := CertificatePage.PromptLabels[I].Top + CertificatePage.PromptLabels[I].Height + ScaleY(4);
  end;
  CertificateSource := TNewComboBox.Create(CertificatePage);
  CertificateSource.Parent := CertificatePage.Surface;
  CertificateSource.Top := 0;
  CertificateSource.Width := CertificatePage.SurfaceWidth;
  CertificateSource.Style := csDropDownList;
  CertificateSource.Items.Add('Use a certificate already on this computer');
  CertificateSource.Items.Add('Import a PFX file');
  CertificateSource.Items.Add('Generate a self-signed certificate (lab use only)');
  CertificateSource.ItemIndex := 0;

  CertCombo := TNewComboBox.Create(CertificatePage);
  CertCombo.Parent := CertificatePage.Surface;
  CertCombo.Top := ScaleY(110);
  CertCombo.Width := CertificatePage.SurfaceWidth;
  CertCombo.Style := csDropDownList;
  CertCombo.OnChange := @CertComboChange;
  PfxBrowseButton := TNewButton.Create(CertificatePage);
  PfxBrowseButton.Parent := CertificatePage.Surface;
  PfxBrowseButton.Top := CertificatePage.Edits[1].Top;
  PfxBrowseButton.Left := CertificatePage.SurfaceWidth - ScaleX(78);
  PfxBrowseButton.Width := ScaleX(78);
  PfxBrowseButton.Height := CertificatePage.Edits[1].Height;
  PfxBrowseButton.Caption := 'Browse...';
  PfxBrowseButton.OnClick := @BrowseCertificate;
  CertificatePage.Edits[1].Width := PfxBrowseButton.Left - ScaleX(8);
  CertificateSource.OnChange := @CertificateSourceChanged;
  CertificateSourceChanged(nil);

  // Checkboxes, not radio buttons: the third argument to CreateInputOptionPage being False is
  // what makes them independent, and this page is expected to grow another option one day.
  ContentPage := CreateInputOptionPage(CertificatePage.ID,
    'Review configuration',
    'Check the installation choices before preparing prerequisites.',
    'Optional product content:', False, False);
  ContentPage.Add('Install the product source code (about 27 MB)');
  // Ticked by default so an operator who clicks through gets what every earlier version
  // installed. Unticking it removes the snapshot after the artifact has been verified, so the
  // signature check still runs against the complete, signed contents.
  ContentPage.Values[0] := True;

  CreateReadinessPage();

  // Parented to the finished page, hidden until there is something to say. A memo rather than a
  // label here for the opposite reason to the readiness page: this page is otherwise empty, so
  // there is room for a real one - and a 64-character API key that cannot be selected would have
  // to be retyped by hand.
  FinishMemo := TNewMemo.Create(WizardForm);
  FinishMemo.Parent := WizardForm.FinishedPage;
  FinishMemo.Left := WizardForm.FinishedLabel.Left;
  FinishMemo.Width := WizardForm.FinishedLabel.Width;
  FinishMemo.Top := WizardForm.FinishedLabel.Top + ScaleY(58);
  FinishMemo.Height := WizardForm.FinishedPage.Height - FinishMemo.Top - ScaleY(38);
  // Both scrollbars, not just the vertical one. Word wrap stays off so the labelled columns line
  // up, which means a 64-character API key is wider than the memo - without a horizontal bar it is
  // silently cut at the right edge, exactly as the first version shipped.
  FinishMemo.ScrollBars := ssBoth;
  FinishMemo.ReadOnly := True;
  FinishMemo.WordWrap := False;
  FinishMemo.Visible := False;

  FinishSaveButton := TNewButton.Create(WizardForm);
  FinishSaveButton.Parent := WizardForm.FinishedPage;
  FinishSaveButton.Left := FinishMemo.Left;
  FinishSaveButton.Top := FinishMemo.Top + FinishMemo.Height + ScaleY(8);
  FinishSaveButton.Width := ScaleX(150);
  FinishSaveButton.Height := ScaleY(23);
  FinishSaveButton.Caption := 'Save this summary...';
  FinishSaveButton.OnClick := @FinishSaveClick;
  FinishSaveButton.Visible := False;
  FinishOpenButton := TNewButton.Create(WizardForm);
  FinishOpenButton.Parent := WizardForm.FinishedPage;
  FinishOpenButton.Left := FinishMemo.Left + ScaleX(160);
  FinishOpenButton.Width := ScaleX(150);
  FinishOpenButton.Height := ScaleY(23);
  FinishOpenButton.Caption := 'Open NodePilot';
  FinishOpenButton.OnClick := @OpenInstalledSite;
  FinishOpenButton.Visible := False;

  // Created here because Inno only accepts pages during InitializeWizard; shown much later, from
  // PrepareToInstall, which is where the installation actually runs.
  ProgressPage := CreateOutputProgressPage('NodePilot setup',
    'The current operation is shown below. Setup verifies the result before continuing.');
  ProgressDetailLabel := TNewStaticText.Create(ProgressPage);
  ProgressDetailLabel.Parent := ProgressPage.Surface;
  ProgressDetailLabel.Top := ScaleY(90);
  ProgressDetailLabel.Width := ProgressPage.SurfaceWidth;
  ProgressDetailLabel.Height := ScaleY(90);
  ProgressDetailLabel.AutoSize := False;
  ProgressDetailLabel.WordWrap := True;
  ProgressLogButton := TNewButton.Create(ProgressPage);
  ProgressLogButton.Parent := ProgressPage.Surface;
  ProgressLogButton.Top := ProgressPage.SurfaceHeight - ScaleY(28);
  ProgressLogButton.Width := ScaleX(140);
  ProgressLogButton.Height := ScaleY(23);
  ProgressLogButton.Caption := 'Open setup log';
  ProgressLogButton.OnClick := @OpenSetupLog;
  ContentViewport := FitSetupToWorkArea(WizardForm, WizardForm.OuterNotebook);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  // With /ANSWERFILE nothing the pages collect is used, so showing them would only invite an
  // operator to fill in values that are then ignored.
  if AnswerFileOverride <> '' then
  begin
    Result := (PageID = ModePage.ID) or (PageID = IdentityPage.ID) or (PageID = AccountPage.ID) or
              (PageID = ProviderPage.ID) or (PageID = SqlPage.ID) or (PageID = PostgresPage.ID) or
              (PageID = PostgresAuthPage.ID) or
              (PageID = NetworkPage.ID) or (PageID = CertificatePage.ID) or
              (PageID = ContentPage.ID) or (PageID = ReadinessPage.ID);
    Exit;

  end;
  if PageID = ModePage.ID then
    Result := (not IsUpgrade) or ForceFullReinstall
  else if IsUpdateSelected() then
    // An update needs no answers: it swaps binaries and leaves the configuration alone.
    Result := (PageID = wpSelectDir) or (PageID = IdentityPage.ID) or (PageID = AccountPage.ID) or
              (PageID = ProviderPage.ID) or (PageID = SqlPage.ID) or (PageID = PostgresPage.ID) or
              (PageID = PostgresAuthPage.ID) or
              (PageID = NetworkPage.ID) or (PageID = CertificatePage.ID) or
              (PageID = ContentPage.ID) or (PageID = ReadinessPage.ID)
  else if PageID = AccountPage.ID then
    Result := IsLocalSystemSelected()
  else if PageID = SqlPage.ID then
    Result := not IsSqlServerSelected()
  else if (PageID = PostgresPage.ID) or (PageID = PostgresAuthPage.ID) then
    Result := IsSqlServerSelected();
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if AnswerFileOverride <> '' then
    Result := 'Configuration file:' + NewLine + Space + AnswerFileOverride
  else if IsUpdateSelected() then
    Result := 'Update existing installation:' + NewLine +
      Space + 'Installed version: ' + ExistingVersion + NewLine +
      Space + 'Service: ' + ExistingServiceName + NewLine +
      Space + 'Program files: ' + ExistingInstallPath + NewLine + NewLine +
      'Setup will stop the service, back up and replace the program files, then restart it.' +
      NewLine + 'Your configuration, data and sign-in details will be kept.'
  else
    Result := MemoDirInfo + NewLine + NewLine +
      'Address:' + NewLine + Space + 'https://' + Trim(NetworkPage.Values[0]) + ':' +
      Trim(NetworkPage.Values[1]) + '/' + NewLine + NewLine +
      'Data:' + NewLine + Space + ExpandConstant('{commonappdata}\NodePilot');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if ContentViewport <> nil then
    SendMessage(ContentViewport.Handle, $0115, 6, 0);
  // Reloaded on every visit rather than cached once: an operator who leaves to import a missing
  // certificate expects to find it in the list on the way back, and the call costs about a second.
  WizardForm.NextButton.Enabled := not AdapterTimedOut;
  if CurPageID = CertificatePage.ID then
  begin
    CertificateSourceChanged(nil);
    LoadCertificateList();
  end;
  if CurPageID = ContentPage.ID then
  begin
    ContentPage.SubCaptionLabel.Caption := 'Address: https://' + Trim(NetworkPage.Values[0]) + ':' + Trim(NetworkPage.Values[1]) + '/' + #13#10 +
      'Program files: ' + ExpandConstant('{app}') + #13#10 +
      'Data: ' + ExpandConstant('{commonappdata}\NodePilot') + #13#10 +
      'Selected prerequisites are applied on the next page. They remain installed if setup is cancelled.';
    ContentPage.SubCaptionLabel.AutoSize := False;
    ContentPage.SubCaptionLabel.WordWrap := True;
    ContentPage.SubCaptionLabel.Height := ScaleY(100);
    ContentPage.CheckListBox.Top := ContentPage.SubCaptionLabel.Top + ContentPage.SubCaptionLabel.Height + ScaleY(16);
    ContentPage.CheckListBox.Height := ScaleY(32);
  end;

  if (CurPageID = ReadinessPage.ID) and (not AdapterRunning) then
    RunProbe();

  if (CurPageID = wpFinished) and (FinishSummary <> '') then
  begin
    // Two short sentences. The previous four-line paragraph wrapped far enough down the page that
    // its last line ran underneath the memo, which sits at a fixed offset below the label's top.
    if FinishHasCredentials then
      WizardForm.FinishedLabel.Caption := 'NodePilot is installed and running.' + #13#10 +
        'Save the values below - the credentials are shown only here.'
    else if IsUpdateSelected() then
      WizardForm.FinishedLabel.Caption := 'NodePilot was updated and is running.' + #13#10 +
        'Your configuration and sign-in details were kept.'
    else
      WizardForm.FinishedLabel.Caption := 'NodePilot is installed and running.' + #13#10 +
        'Review the installation details below.';
    // Measured off the label instead of assuming its height: the caption above is short today, and
    // a translation or an extra sentence would silently push it back under the memo.
    FinishMemo.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
    FinishMemo.Height := WizardForm.FinishedPage.Height - FinishMemo.Top - ScaleY(38);
    FinishMemo.Text := FinishSummary;
    FinishMemo.Visible := True;
    FinishSaveButton.Top := FinishMemo.Top + FinishMemo.Height + ScaleY(8);
    FinishSaveButton.Visible := True;
    FinishOpenButton.Top := FinishSaveButton.Top;
    FinishOpenButton.Visible := FinishUrl <> '';
  end;
end;

function ValidatePort(const Value: String; const AllowZero: Boolean; const Name: String): Boolean;
var
  Port: Integer;
begin
  Port := StrToIntDef(Trim(Value), -1);
  if (Port < 0) or (Port > 65535) or ((Port = 0) and (not AllowZero)) then
  begin
    MsgBox(Name + ' must be a number between ' + IntToStr(Ord(not AllowZero)) + ' and 65535.', mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  Thumbprint: String;
  UninstPath: String;
  I: Integer;
begin
  Result := True;

  if (CurPageID = ModePage.ID) and (ModePage.SelectedValueIndex = 2) then
  begin
    UninstPath := UninstallerPath();
    if UninstPath = '' then
    begin
      MsgBox('This installation was deployed from the zip package, so it has no setup ' +
        'uninstaller to hand over to.' + #13#10#13#10 + 'Remove it with this script instead, ' +
        'from an elevated PowerShell:' + #13#10#13#10 +
        '  ' + ExistingInstallPath + '\deploy\Uninstall-NodePilot.ps1' + #13#10#13#10 +
        'Add -PurgeData to delete the data directory as well.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    // No confirmation here on purpose: the uninstaller asks its own question - whether to keep
    // the data directory - and two prompts in a row for one decision is how people learn to
    // click through prompts. Launched without waiting so it takes over the screen instead of
    // running behind a setup window frozen for the duration.
    if not Exec(UninstPath, '', '', SW_SHOW, ewNoWait, ResultCode) then
    begin
      MsgBox('The uninstaller could not be started:' + #13#10#13#10 +
        SysErrorMessage(ResultCode), mbError, MB_OK);
      Result := False;
      Exit;
    end;
    UninstallHandoff := True;
    Result := False;
    WizardForm.Close;
    Exit;
  end;

  if (CurPageID = ModePage.ID) and (ModePage.SelectedValueIndex = 1) then
  begin
    // The sharpest edge in the whole design, and invisible everywhere else: a full re-setup
    // re-renders appsettings.Production.json, and the old External-Trigger API key is not
    // recoverable from anywhere. Every external caller breaks.
    Result := MsgBox('Setting up from scratch rewrites the configuration and issues a NEW ' +
      'External-Trigger API key.' + #13#10#13#10 + 'The current key cannot be recovered afterwards, ' +
      'and anything calling NodePilot with it will stop working.' + #13#10#13#10 + 'Continue?',
      mbConfirmation, MB_YESNO) = IDYES;
    Exit;
  end;

  if CurPageID = AccountPage.ID then
  begin
    if Pos('\', Trim(AccountPage.Values[0])) = 0 then
    begin
      MsgBox('Enter the account as DOMAIN\name$.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  if CurPageID = SqlPage.ID then
  begin
    if Trim(SqlPage.Values[0]) = '' then
    begin
      MsgBox('Enter the SQL Server host.', mbError, MB_OK);
      Result := False;
    end
    else if Trim(SqlPage.Values[1]) = '' then
    begin
      MsgBox('Enter the database name.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  if CurPageID = PostgresPage.ID then
  begin
    if Trim(PostgresPage.Values[0]) = '' then
    begin
      MsgBox('Enter the PostgreSQL host.', mbError, MB_OK);
      Result := False;
    end
    else if not ValidatePort(PostgresPage.Values[1], False, 'The PostgreSQL port') then
      Result := False
    else if Trim(PostgresPage.Values[2]) = '' then
    begin
      MsgBox('Enter the database name.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  if CurPageID = PostgresAuthPage.ID then
  begin
    if Trim(PostgresAuthPage.Values[0]) = '' then
    begin
      MsgBox('Enter the PostgreSQL user.', mbError, MB_OK);
      Result := False;
    end
    else if PostgresAuthPage.Values[1] = '' then
    begin
      MsgBox('Enter the password for the PostgreSQL user.', mbError, MB_OK);
      Result := False;
    end
    else if not FileExists(Trim(PostgresAuthPage.Values[2])) then
    begin
      MsgBox('The root certificate file was not found. NodePilot connects with ' +
             'SSL Mode=VerifyFull and needs the PEM file that signed the server certificate.',
             mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  if CurPageID = NetworkPage.ID then
  begin
    if Trim(NetworkPage.Values[0]) = '' then
    begin
      MsgBox('Enter the public host name clients will use.', mbError, MB_OK);
      Result := False;
    end
    else if not ValidatePort(NetworkPage.Values[1], False, 'The HTTPS port') then Result := False
    else if not ValidatePort(NetworkPage.Values[2], True, 'The HTTP port') then Result := False
    else if NetworkPage.Values[1] = NetworkPage.Values[2] then
    begin
      MsgBox('HTTP and HTTPS need different ports. Use 0 to disable HTTP.', mbError, MB_OK);
      Result := False;
    end;
    Exit;
  end;

  if CurPageID = CertificatePage.ID then
  begin
    if CertificateSource.ItemIndex = 1 then
    begin
      if not FileExists(Trim(CertificatePage.Values[1])) then
      begin
        MsgBox('Select an existing PFX file.', mbError, MB_OK);
        Result := False;
      end;
    end
    else
    begin
      Thumbprint := Uppercase(Trim(CertificatePage.Values[0]));
      StringChangeEx(Thumbprint, ' ', '', True);
      StringChangeEx(Thumbprint, #$200E, '', True);
      CertificatePage.Values[0] := Thumbprint;
      if (Thumbprint <> '') and (Length(Thumbprint) <> 40) then Result := False;
      for I := 1 to Length(Thumbprint) do
        if Pos(Thumbprint[I], '0123456789ABCDEF') = 0 then Result := False;
      if not Result then MsgBox('A certificate thumbprint must contain 40 hexadecimal characters.', mbError, MB_OK);
    end;
    Exit;
  end;

  if CurPageID = ReadinessPage.ID then
  begin

    if ProbeBlocking then
    begin
      // No line may START with '#13#10' - the preprocessor reads a leading '#' as a directive.
      MsgBox('These requirements are not met:' + #13#10#13#10 + BlockingTitles + #13#10 +
             'Select a line on the page to see what to do about it, then choose "Check again".' + #13#10#13#10 +
             'Continuing would fail during installation - Install-NodePilot.ps1 enforces the same ' +
             'checks. Full output: ' + ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log',
             mbError, MB_OK);
      Result := False;
    end
    else if not ProbeRan then
      Result := False;
    Exit;
  end;
end;

// ---------------------------------------------------------------------------
// Session lifetime and installation
// ---------------------------------------------------------------------------

// The installation runs HERE, not in ssPostInstall.
//
// ssPostInstall is the obvious place - the files are on disk by then - but measured on Inno 6.7.3
// neither RaiseException nor Abort in that step changes the exit code: a failed install still
// reports 0. Under SCCM that is a deployment which claims success and installed nothing.
// PrepareToInstall returns a message instead and setup exits 7, so failure is visible to whatever
// launched it. Everything the installation needs is extracted to {tmp} for the same reason.
// Runs the adapter and draws its progress until it reports an outcome.
//
// Synchronous Exec was the obvious choice and the wrong one: it blocks Inno's UI thread for the
// entire installation, so the window stops repainting and Windows is entitled to grey it out as
// "Not responding". It did, and that was read as a crash.
//
// The exit code comes from result.ini rather than from Exec. With ewNoWait there is no exit code to
// read, and the adapter writes that file in a finally block - so it exists whatever happened,
// including on the paths that roll back.
function OpenProcess(Access: LongWord; Inherit: Boolean; ProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: LongWord; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function RunAdapterWithProgress(const Arguments, ResultIni, ProgressPath: String;
  var ExitCode: Integer): String;
var
  Elapsed, SeenLines, Shown, HasTotal, LastTotal, LastShown, I: Integer;
  Lines: TArrayOfString;
  Line, Reported, State, Phase, MessageText, Detail: String;
  ProcessHandle: LongWord;
  PidText: AnsiString;
begin
  Result := '';
  ExitCode := -1;
  if AdapterTimedOut then begin Result := 'A previous operation may still be running. Close setup and check its log before retrying.'; Exit; end;
  DeleteFile(ResultIni);
  DeleteFile(ProgressPath);
  DeleteFile(ProgressPath + '.pid');
  if not StartPowerShell(Arguments) then begin Result := 'Could not start PowerShell.'; Exit; end;
  SeenLines := 0;
  Elapsed := 0;
  Shown := 0;
  HasTotal := 0;
  LastTotal := -1;
  LastShown := -1;
  ProcessHandle := 0;
  MessageText := 'Preparing the requested operation';
  Detail := '';
  AdapterRunning := True;
  if not WizardSilent() then ProgressPage.Show();
  try
    repeat
      if not WizardSilent() then
      begin
        if HasTotal = 1 then
        begin
          if (LastTotal <> HasTotal) or (LastShown <> Shown) then
            ProgressPage.SetProgress(Shown, 100);
        end
        else if LastTotal <> HasTotal then
        begin
          ProgressPage.ProgressBar.Visible := True;
          ProgressPage.ProgressBar.Style := npbstMarquee;
        end;
        LastTotal := HasTotal;
        LastShown := Shown;
        ProgressPage.SetText(MessageText, '');
        ProgressDetailLabel.Caption := Detail + #13#10 + 'Elapsed: ' + IntToStr(Elapsed div 1000) + ' seconds';
      end;
      Sleep(AdapterPollMs);
      Elapsed := Elapsed + AdapterPollMs;
      if LoadStringsFromFile(ProgressPath, Lines) then
        for I := SeenLines to GetArrayLength(Lines) - 1 do
        begin
          Line := StripBom(Lines[I]);
          State := PopField(Line);
          Phase := PopField(Line);
          Reported := PopField(Line);
          HasTotal := StrToIntDef(PopField(Line), 0);
          if Pos('|end', Line) = 0 then Break;
          Shown := StrToIntDef(Reported, 0);
          MessageText := PopField(Line);
          Detail := PopField(Line);
          SeenLines := I + 1;
        end;
      if FileExists(ResultIni) then
      begin
        Reported := GetIniString('summary', 'exitCode', '', ResultIni);
        if Reported <> '' then
        begin
          ExitCode := StrToIntDef(Reported, 1);
          Exit;
        end;
      end;
      if (ProcessHandle = 0) and LoadStringFromFile(ProgressPath + '.pid', PidText) then
        ProcessHandle := OpenProcess($00100000, False, StrToIntDef(String(PidText), 0));
      if (ProcessHandle <> 0) and (WaitForSingleObject(ProcessHandle, 0) = 0) then
      begin
        // Re-read after process exit so a completed result cannot race the last poll.
        Reported := GetIniString('summary', 'exitCode', '', ResultIni);
        if Reported <> '' then ExitCode := StrToIntDef(Reported, 1)
        else Result := 'The setup worker stopped without reporting a result. Open the setup log for details.';
        Exit;
      end;
    until Elapsed >= AdapterTimeoutMs;
    AdapterTimedOut := True;
    Result := 'This operation exceeded 45 minutes and may still be running. Close setup and check the log before retrying. The protected setup session was retained.';
  finally
    if ProcessHandle <> 0 then CloseHandle(ProcessHandle);
    if not WizardSilent() then ProgressPage.Hide();
    AdapterRunning := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AnswerMode, Arguments, ResultIni, ProvisionIni, DbStatus, Problems, Extra: String;
begin
  if AdapterTimedOut then
  begin
    Result := 'A previous operation may still be running. Close setup and inspect the log before retrying.';
    Exit;
  end;
  Result := EnsureSession();
  if Result <> '' then Exit;

  ExtractTemporaryFile('{#ArtifactFileName}');
  ExtractTemporaryFile('{#ArtifactFileName}.manifest.json');
  ExtractTemporaryFile('{#ArtifactFileName}.manifest.json.p7s');
  ExtractTemporaryFile('nodepilot-release-signing.cer');

  if IsUpdateSelected() then AnswerMode := 'update' else AnswerMode := 'install';
  WriteAnswerFile(AnswerMode, False);
  ResultIni := SessionDir + '\result.ini';

  // Silent runs get their provisioning here, because the readiness page - the only thing that
  // ever ran it - does not exist on this path. Without this the provisioning keys were dead
  // weight in an unattended answer file: accepted, validated, and then quietly ignored, which is
  // exactly how a fleet rollout ends up with a service that starts and answers 503 because the
  // computer account was never granted db_owner.
  //
  // Run unconditionally rather than after parsing the file for a "does it ask for anything"
  // flag: Pascal Script has no JSON reader, the adapter already has one, and a run with nothing
  // requested performs no action and exits 0.
  //
  // /ANSWERFILE is deliberately NOT filtered through AnswerMode. That variable comes from
  // IsUpdateSelected(), which reads ModePage.SelectedValueIndex - and /ANSWERFILE skips the mode
  // page, so the index keeps its hard default of 0 ('update'). Any host that already carried a
  // NodePilot installation therefore turned an answer file saying "mode": "install" into
  // AnswerMode = 'update' and silently dropped every provisioning key: no database, no login, no
  // generated certificate, no runtime. The file decides on this path, by the same reasoning as
  // above - the adapter validates it, update mode accepts no provisioning keys at all, so a
  // Provision run for an update answer file performs no action and exits 0.
  if WizardSilent() and ((AnswerFileOverride <> '') or (AnswerMode = 'install')) then
  begin
    // Unattended runs never reached the readiness page, so this is the first and only chance to
    // put lazy dontcopy payloads where the adapter looks for them.
    EnsurePgClient();
    EnsureRuntimePayload();
    ProvisionIni := SessionDir + '\provision.ini';
    if not RunPowerShell('-Mode Provision -AnswerFile "' + AnswerFilePath() + '" -OutFile "' +
      ProvisionIni + '"', ResultCode) or (ResultCode <> 0) then
    begin
      Result := 'The provisioning requested by the answer file could not be applied (exit code ' +
        IntToStr(ResultCode) + '). Log: ' + ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log';
      Exit;
    end;

    // A failed database provisioning exits 0 and reports itself INSIDE provision.ini, exactly as
    // it does for the readiness page - which reads this same value and stops. Without the check
    // the unattended path walked on to Apply and died in the SQL pre-flight instead, telling the
    // operator to have a DBA create a login that was never the problem.
    DbStatus := GetIniString('provision.database', 'status', '', ProvisionIni);
    if (DbStatus <> '') and (DbStatus <> 'Pass') then
    begin
      Result := 'The database could not be prepared: ' +
        ExpandNewlines(GetIniString('provision.database', 'detail', '', ProvisionIni));
      Exit;
    end;

    // The same readback for the other two fixes. A fleet rollout whose runtime installation failed
    // used to walk on to Apply and die in the pre-flight instead, naming a runtime the answer file
    // had just asked the setup to install.
    Problems := Trim(ProvisionSectionProblem(ProvisionIni, 'provision.runtime', 'The ASP.NET Core runtime') +
      ProvisionSectionProblem(ProvisionIni, 'provision.signer', 'The publisher certificate') +
      ProvisionSectionProblem(ProvisionIni, 'provision.gmsa', 'The service account'));
    if Problems <> '' then
    begin
      Result := 'The provisioning requested by the answer file did not complete. ' + Problems;
      Exit;
    end;
  end;

  // -Mode Apply, not Install or Update: the answer file already declares which it is, and a
  // second place to say so is a second place for them to disagree. That matters most with
  // /ANSWERFILE, where the file comes from outside the wizard entirely.
  Arguments := '-Mode Apply' +
    ' -AnswerFile "' + AnswerFilePath() + '"' +
    ' -ArtifactPath "' + ExpandConstant('{tmp}\{#ArtifactFileName}') + '"' +
    ' -TrustedArtifactSignerThumbprint "{#SignerThumbprint}"' +
    ' -ProgressFile "' + SessionDir + '\progress.txt"' +
    ' -OutFile "' + ResultIni + '"';

  Result := RunAdapterWithProgress(Arguments, ResultIni, SessionDir + '\progress.txt', ResultCode);
  if Result <> '' then Exit;

  if ResultCode <> 0 then
  begin
    // Expanded, not raw: the adapter escapes newlines to keep an INI value on one line, so an
    // unexpanded message shows a literal \n in the middle of the sentence the operator has to read.
    Extra := ExpandNewlines(GetIniString('summary', 'error', '', ResultIni));
    if ResultCode = ExitInstallFailed then
      Extra := Extra + ' The installation was rolled back.'
    else if ResultCode = ExitAnswerFileInvalid then
      Extra := Extra + ' The answer file was rejected.';
    Result := 'NodePilot installation failed (exit code ' + IntToStr(ResultCode) + '). ' + Extra +
      ' Log: ' + GetIniString('summary', 'logPath', '', ResultIni);
    Exit;
  end;

  // Only on the success path: the finish page must never present values from a run that was
  // rolled back. Built here rather than in CurPageChanged because the session directory - and
  // with it result.ini - is wiped by the Cleanup mode before the wizard closes.
  BuildFinishSummary(ResultIni);
end;

// ---------------------------------------------------------------------------
// Uninstall
// ---------------------------------------------------------------------------
//
// The uninstall removes everything this setup installed - the Windows service, the service
// binaries, the firewall rules, the installation marker - and asks about exactly one thing: the
// data directory.
//
// Databases are preserved even when setup provisioned them. Their removal remains a DBA decision.
//
// The data directory IS ours - logs, the JWT signing key, the data-protection keyring - so that
// one is a genuine choice, defaulting to KEEP everywhere: interactive, /SILENT with no switches,
// Apps & Features, and Inno's own [UninstallRun]. An uninstall that destroys data because nobody
// said otherwise is not a defensible default, and the unattended path is exactly where nobody is
// watching.
//
// SuppressibleTaskDialogMsgBox is what makes "also usable silently" work without a second code
// path: interactive it asks, and under /SUPPRESSMSGBOXES it returns the default given here.

procedure InitializeUninstallProgressForm();
begin
  FitSetupToWorkArea(UninstallProgressForm, UninstallProgressForm.OuterNotebook);
end;

function InitializeUninstall(): Boolean;
var
  Response: Integer;
begin
  Result := True;
  UninstallPurgeData := ExpandConstant('{param:PURGEDATA|0}') <> '0';

  // An explicit switch is an answer; do not ask again.
  if UninstallSilent() or UninstallPurgeData then Exit;

  Response := SuppressibleTaskDialogMsgBox(
    'Keep NodePilot''s data directory?',
    'Everything else goes: the Windows service, the program files, the firewall rules.' + #13#10#13#10 +
    'The data directory holds the logs, the JWT signing key and the data-protection keyring.' + #13#10 +
    'Keeping it lets a later reinstall pick up where this one left off; deleting it cannot be undone.' + #13#10#13#10 +
    'Your database is kept either way, including databases created by setup.' + #13#10 +
    'Remove it separately only when you are certain it is no longer needed.',
    mbConfirmation, MB_YESNO, ['Keep the data directory', 'Delete the data directory'], 0, IDYES);
  UninstallPurgeData := Response = IDNO;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  InstalledInstallPath: String;
begin
  if CurStep = ssPostInstall then
  begin
    // Apps & Features shows InstallLocation, and Inno fills it with {app} - which is where the
    // uninstaller ended up, not where NodePilot was installed. /ANSWERFILE skips the directory
    // page, so {app} keeps DefaultDirName while the adapter installs to the answer file's
    // installPath; the entry then points an operator at a directory holding the uninstaller and
    // nothing else. Corrected from the marker the installer has just written, which is the same
    // source the uninstaller reads.
    //
    // Best-effort on purpose. An exception in ssPostInstall does NOT change the exit code (see the
    // note in [Files]), so nothing load-bearing may live here - a failed write leaves Inno's own
    // value in place, which is exactly today's behaviour.
    if RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'InstallPath', InstalledInstallPath) and
       (InstalledInstallPath <> '') then
      RegWriteStringValue(HKLM64,
        'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{03EAD540-1472-4A1B-9F06-9CB3D358E202}_is1',
        'InstallLocation', AddBackslash(InstalledInstallPath));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ScriptPath, Arguments, Switches: String;
  InstalledServiceName, InstalledInstallPath, InstalledDataPath: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // usUninstall runs before Inno deletes the files it installed, so the script is still there.
    ScriptPath := ExpandConstant('{app}\deploy\Uninstall-NodePilot.ps1');
    if not FileExists(ScriptPath) then
    begin
      SuppressibleMsgBox('The NodePilot uninstall script is missing:' + #13#10 + ScriptPath + #13#10#13#10 +
        'The Windows service and its firewall rules have NOT been removed. Run ' +
        'Uninstall-NodePilot.ps1 by hand, or reinstall and uninstall again.',
        mbCriticalError, MB_OK, IDOK);
      Exit;
    end;

    // Built HERE, at uninstall time. This is the whole reason the [UninstallRun] section is gone.
    Switches := '';
    if UninstallPurgeData then Switches := Switches + ' -PurgeData';

    // Read back what was actually installed, from the marker Install-NodePilot.ps1 writes.
    // GetServiceName('') resolves to the literal 'NodePilot' in the uninstaller process -
    // ExistingServiceName is only ever populated by DetectExistingInstallation(), which runs in
    // Setup - and {app} is merely where Inno put the uninstaller, which is NOT installPath when
    // /ANSWERFILE supplied one (the dir page never ran). Passing those guesses meant an install
    // with a non-default service name or path was "uninstalled" with exit 0 while the service,
    // its firewall rule and every program file stayed exactly where they were, and only the
    // bookkeeping that said NodePilot existed was removed. -DataPath was not passed at all, so
    // -PurgeData wiped the default directory or nothing.
    if (not RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'ServiceName', InstalledServiceName)) or
       (InstalledServiceName = '') then
      InstalledServiceName := GetServiceName('');
    if (not RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'InstallPath', InstalledInstallPath)) or
       (InstalledInstallPath = '') then
      InstalledInstallPath := ExpandConstant('{app}');
    if not RegQueryStringValue(HKLM64, 'SOFTWARE\NodePilot\Server', 'DataPath', InstalledDataPath) then
      InstalledDataPath := '';

    Arguments := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
      ' -ServiceName "' + InstalledServiceName + '"' +
      ' -InstallPath "' + InstalledInstallPath + '"';
    // Only when known: Uninstall-NodePilot.ps1's own default is the right fallback, and an empty
    // -DataPath "" would point it at the current directory.
    if InstalledDataPath <> '' then
      Arguments := Arguments + ' -DataPath "' + InstalledDataPath + '"';
    Arguments := Arguments + Switches;

    if not Exec('powershell.exe', Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      SuppressibleMsgBox('Could not start PowerShell to remove the NodePilot service.',
        mbCriticalError, MB_OK, IDOK)
    else if ResultCode <> 0 then
      // Reported, not swallowed. The script exits non-zero when it had to leave files behind, and
      // an operator who is told that can act on it.
      SuppressibleMsgBox('The NodePilot uninstall reported problems (exit code ' +
        IntToStr(ResultCode) + ').' + #13#10#13#10 +
        'Check %TEMP% for details; some files or the Windows service may still be present.',
        mbError, MB_OK, IDOK);
  end;

  if CurUninstallStep = usPostUninstall then
    RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\NodePilot\Server');
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if AdapterRunning then
  begin
    Cancel := False;
    Confirm := False;
    Exit;
  end;
  // Closing the wizard to hand over to the uninstaller is not the user cancelling anything, so
  // do not ask them to confirm an abort they never asked for. DeinitializeSetup still runs and
  // still wipes the session directory.
  if UninstallHandoff then
    Confirm := False;
end;

procedure DeinitializeSetup();
var
  ResultCode: Integer;
begin
  // Covers a cancel halfway through the wizard as well as a completed run: the answer file holds
  // the database password and must not outlive setup either way.
  if (SessionDir <> '') and (not AdapterTimedOut) then
    RunPowerShell('-Mode Cleanup -SessionPath "' + SessionDir + '" -AnswerFile "' +
      AnswerFilePath() + '"', ResultCode);
end;
