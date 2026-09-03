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
; Larger than the default so the prerequisites page fits. Each check row renders as
; "Title: Detail", and at the default width most rows wrap, leaving the remediation box too small
; to show a CREATE LOGIN / CREATE USER block. Extra width unwraps most rows and extra height gives
; the remediation box room.
;
; WizardResizable stays off: the controls on the network and prerequisites pages are positioned
; once at wizard construction and carry no anchors, so a resized window would leave them behind.
; A fixed larger start size is safe because every control that must grow is sized from SurfaceWidth.
WizardSizePercent=125,145
SetupIconFile={#StageDir}\setup-icon.ico
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
Source: "{#StageDir}\payload\*";    Flags: dontcopy

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
  ModePage: TInputOptionWizardPage;
  IdentityPage: TInputOptionWizardPage;
  AccountPage: TInputQueryWizardPage;
  ProviderPage: TInputOptionWizardPage;
  SqlPage: TInputQueryWizardPage;
  PostgresPage: TInputQueryWizardPage;
  PostgresAuthPage: TInputQueryWizardPage;
  NetworkPage: TInputQueryWizardPage;
  ContentPage: TInputOptionWizardPage;
  ReadinessPage: TWizardPage;

  CheckIds: array[0..CheckCount - 1] of String;
  CheckLabels: array[0..CheckCount - 1] of TNewStaticText;
  CheckMarks: array[0..CheckCount - 1] of TNewStaticText;
  CheckFixes: array[0..CheckCount - 1] of TNewCheckBox;
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

  // Whether the bundled psql client has been extracted to {tmp}. Extraction is attempted at most
  // once per run, whether or not this build carries a client.
  PgClientExtracted: Boolean;

  // The runtime installer is a dontcopy payload too. Interactive auto-fix runs before
  // PrepareToInstall, so extraction cannot wait for that phase, and the helper is shared by the
  // silent and interactive paths, so it stays idempotent.
  RuntimePayloadExtracted: Boolean;

  // Drawn while the adapter installs, which takes minutes and would otherwise show no progress.
  ProgressPage: TOutputProgressWizardPage;

  // Finish page. The values shown here are available nowhere else: the API key is generated by
  // the adapter, printed to a hidden console, and left out of install-report.txt by design.
  FinishMemo: TNewMemo;
  FinishSaveButton: TNewButton;
  FinishSummary: String;

  SessionDir: String;
  AnswerFileOverride: String;
  ExistingInstallPath: String;
  ExistingServiceName: String;
  ExistingVersion: String;
  IsUpgrade: Boolean;
  ForceFullReinstall: Boolean;
  UninstallPurgeData: Boolean;
  UninstallHandoff: Boolean;
  ProbeRan: Boolean;
  ProbeBlocking: Boolean;

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
  try
    ExtractTemporaryFiles('psql.exe');
    ExtractTemporaryFiles('*.dll');
  except
    // Built without the client. The Postgres row says so on its own.
  end;
end;

procedure EnsureRuntimePayload();
begin
  if RuntimePayloadExtracted then Exit;
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
      // Visible as well as checked: a hidden box can still carry a tick from an earlier probe,
      // and a fix the operator cannot see is not one they asked for.
      Result := CheckFixes[I].Visible and CheckFixes[I].Checked;
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
  AddLine(Lines, Count, '    "thumbprint": ' + JsonString(Trim(NetworkPage.Values[4])) + ',');
  AddLine(Lines, Count, '    "source": "existing"');

  // The probe file carries no fix flags; it only asks. It does carry the PostgreSQL superuser,
  // because those credentials decide whether the Postgres row may offer a fix at all.
  if ForProbe and (not IsSqlServerSelected()) and (Trim(PostgresAuthPage.Values[3]) <> '') then
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
    AddLine(Lines, Count, '    "generateSelfSignedCertificate": ' + JsonBool(IsFixRequested('certificate')) + ',');
    // Two rows, one key: Provision-NodePilotDatabase.ps1 is existence-guarded throughout, so one
    // run covers both a missing database and a missing grant for the service identity. On the
    // Postgres path the same key routes to Provision-NodePilotPostgres.ps1; the provider decides
    // which script runs, so no second flag can contradict it.
    AddLine(Lines, Count, '    "createDatabaseAndLogin": ' +
      JsonBool(IsFixRequested('database') or IsFixRequested('databaseServiceLogin')) + ',');
    if not IsSqlServerSelected() then
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

procedure UpdateRemediation(Index: Integer);
var
  Ini: String;
  Hint, Remediation: String;
begin
  Ini := SessionDir + '\probe.ini';
  Hint := GetIniString('check.' + CheckIds[Index], 'hint', '', Ini);
  Remediation := GetIniString('check.' + CheckIds[Index], 'remediation', '', Ini);
  if (Hint = '') and (Remediation = '') then
    RemediationText := 'Nothing to do for this item.'
  else
    RemediationText := ExpandNewlines(Hint) + #13#10#13#10 + ExpandNewlines(Remediation);
  RemediationBox.Text := RemediationText;
end;

procedure CheckLabelClick(Sender: TObject);
var
  I: Integer;
begin
  // Either half of a row is a valid click target: the glyph sits in its own control and does the
  // same thing as the text.
  for I := 0 to CheckCount - 1 do
    if (CheckLabels[I] = Sender) or (CheckMarks[I] = Sender) then
      UpdateRemediation(I);
end;

// Rows are placed here rather than at construction time so that hidden auto-fix checkboxes claim
// no vertical space and the remediation area keeps room.
procedure LayoutReadiness();
var
  I, Y, ButtonTop, FixTop, FixFloor, FixCount, Available: Integer;
begin
  ButtonTop := ReadinessPage.SurfaceHeight - ScaleY(24);

  // Counted before anything is placed, because the clamp below is about the last fix box: with N
  // of them the first may sit no lower than N*19 px above the buttons, so the last one still
  // lands on the page. The page does not scroll, and a checkbox drawn behind the buttons cannot
  // be ticked. Clamping overlaps the text above it, which is visible rather than missing.
  FixCount := 0;
  for I := 0 to CheckCount - 1 do
    if CheckLabels[I].Visible and CheckFixes[I].Visible then FixCount := FixCount + 1;
  FixFloor := ButtonTop - ScaleY(19) * FixCount;

  Y := 0;
  for I := 0 to CheckCount - 1 do
  begin
    CheckMarks[I].Visible := CheckLabels[I].Visible;
    if not CheckLabels[I].Visible then Continue;

    CheckLabels[I].Top := Y;
    CheckMarks[I].Top := Y;
    Y := Y + CheckLabels[I].Height + ScaleY(3);

    if CheckFixes[I].Visible then
    begin
      FixTop := Y;
      if FixTop > FixFloor then FixTop := FixFloor;
      CheckFixes[I].Top := FixTop;
      // Each box claims its own strip, so two clamped ones keep their order instead of landing on
      // top of each other.
      FixFloor := FixFloor + ScaleY(19);
      Y := Y + ScaleY(19);
    end;
  end;

  RemediationBox.Top := Y + ScaleY(8);
  Available := ButtonTop - ScaleY(6) - RemediationBox.Top;
  // Never negative and never overlapping the buttons. The floor is two lines plus the scrollbar,
  // because at one line the control reads as a broken edit field. It is reached only under
  // high-DPI scaling or if another check is added. Rows keep their space and this box gives it
  // up: an explanation that has to scroll is better than a check nobody can see.
  if Available < ScaleY(34) then Available := ScaleY(34);
  RemediationBox.Height := Available;
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

procedure RunProbe();
var
  ResultCode: Integer;
  Ini, Status, Title, Detail, AutoFixLabel, SessionError: String;
  I: Integer;
begin
  WizardForm.Update();
  SessionError := EnsureSession();
  if SessionError <> '' then
  begin
    MsgBox(SessionError, mbCriticalError, MB_OK);
    ProbeRan := False;
    Exit;
  end;
  // Before the answer file, because the Postgres row's verdict depends on whether the client is
  // there to log in with.
  if not IsSqlServerSelected() then EnsurePgClient();

  WriteAnswerFile('install', True);
  Ini := SessionDir + '\probe.ini';
  DeleteFile(Ini);

  if not RunPowerShell('-Mode Probe -AnswerFile "' + AnswerFilePath() + '" -OutFile "' + Ini + '"', ResultCode) then
  begin
    MsgBox('Could not start PowerShell to check the prerequisites.', mbCriticalError, MB_OK);
    ProbeRan := False;
    Exit;
  end;
  if (ResultCode <> 0) and (ResultCode <> ExitProbeFailed) then
  begin
    MsgBox('The prerequisite check failed to run (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
           'See ' + GetIniString('summary', 'logPath', '%TEMP%\nodepilot-server-setup.log', Ini) + '.',
           mbCriticalError, MB_OK);
    ProbeRan := False;
    Exit;
  end;

  for I := 0 to CheckCount - 1 do
  begin
    Status := GetIniString('check.' + CheckIds[I], 'status', '', Ini);
    Title := GetIniString('check.' + CheckIds[I], 'title', CheckIds[I], Ini);
    Detail := GetIniString('check.' + CheckIds[I], 'detail', '', Ini);
    AutoFixLabel := GetIniString('check.' + CheckIds[I], 'autoFixLabel', '', Ini);

    if Status = '' then
    begin
      CheckLabels[I].Caption := '';
      CheckLabels[I].Visible := False;
      CheckMarks[I].Visible := False;
      CheckFixes[I].Visible := False;
      Continue;
    end;

    CheckLabels[I].Visible := True;
    CheckLabels[I].Caption := Title + ': ' + Detail;
    CheckMarks[I].Visible := True;

    // A glyph as well as a colour, so the status stays readable without colour vision and in a
    // greyscale screenshot.
    if Status = 'Pass' then
    begin
      CheckMarks[I].Caption := MarkPass;
      CheckLabels[I].Font.Color := ColourPass;
      CheckMarks[I].Font.Color := ColourPass;
    end
    else if Status = 'Fail' then
    begin
      CheckMarks[I].Caption := MarkFail;
      CheckLabels[I].Font.Color := ColourFail;
      CheckMarks[I].Font.Color := ColourFail;
    end
    else if Status = 'Warn' then
    begin
      CheckMarks[I].Caption := MarkWarn;
      CheckLabels[I].Font.Color := ColourWarn;
      CheckMarks[I].Font.Color := ColourWarn;
    end
    else
    begin
      CheckMarks[I].Caption := MarkSkip;
      CheckLabels[I].Font.Color := clGray;
      CheckMarks[I].Font.Color := clGray;
    end;

    // A fix is offered for a failed or warning row that the adapter says it can act on. Ticking
    // one and clicking Next runs Provision and then re-runs this probe; the fix is never assumed
    // to have worked. Warning rows can carry a fix too, such as importing the publisher
    // certificate, which is optional because the installation verifies the signature against a
    // pinned thumbprint. canAutoFix and the label are the gate, so no checkbox appears without a
    // fix behind it.
    CheckFixes[I].Visible := ((Status = 'Fail') or (Status = 'Warn')) and
      (GetIniString('check.' + CheckIds[I], 'canAutoFix', '0', Ini) = '1') and
      (AutoFixLabel <> '');
    CheckFixes[I].Caption := AutoFixLabel;
    if not CheckFixes[I].Visible then
      CheckFixes[I].Checked := False
    // Some fixes arrive ticked; see AutoFixDefault in Preflight.ps1. The box is still shown and
    // still clearable, so the default only saves a click.
    else if (not CheckFixDefaulted[I]) and
      (GetIniString('check.' + CheckIds[I], 'autoFixDefault', '0', Ini) = '1') then
    begin
      CheckFixes[I].Checked := True;
      CheckFixDefaulted[I] := True;
    end;
  end;

  ProbeRan := True;
  ProbeBlocking := ResultCode = ExitProbeFailed;
  RemediationText := 'Select a line above to see what to do about it.';
  RemediationBox.Text := RemediationText;
  // Last, because it measures the wrapped heights the captions above just produced.
  LayoutReadiness();
end;

procedure RecheckClick(Sender: TObject);
begin
  RunProbe();
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
  Target: String;
begin
  Target := ExpandConstant('{userdesktop}\nodepilot-setup-summary.txt');
  if SaveTextToFile(Target, FinishSummary) then
    MsgBox('Saved to ' + Target + #13#10#13#10 +
      'It contains the setup token and the API key. Move it somewhere safe and delete it from ' +
      'the desktop once you are done.', mbInformation, MB_OK)
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
  Token := GetIniString('result', 'adminSetupToken', '', ResultIni);
  ApiKey := GetIniString('result', 'externalTriggerApiKey', '', ResultIni);
  Thumb := GetIniString('result', 'certificateThumbprint', '', ResultIni);
  InstallDir := GetIniString('result', 'installPath', '', ResultIni);
  DataDir := GetIniString('result', 'dataPath', '', ResultIni);
  Service := GetIniString('result', 'serviceName', '', ResultIni);

  BootstrapUser := GetIniString('bootstrap', 'username', '', ResultIni);
  BootstrapPassword := GetIniString('bootstrap', 'password', '', ResultIni);

  // A label per line with the value straight after it. The memo has no word wrap, so long prose
  // lines would be cut off at the right edge.
  S := '';
  if Url <> '' then S := S + 'Address       ' + Url + #13#10;
  if Service <> '' then S := S + 'Service       ' + Service + #13#10;
  if InstallDir <> '' then S := S + 'Program       ' + InstallDir + #13#10;
  if DataDir <> '' then S := S + 'Data          ' + DataDir + #13#10;
  if Thumb <> '' then S := S + 'Certificate   ' + Thumb + #13#10;

  // Credentials last and in their own block, so they are not missed among the paths above.
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
procedure CompactNetworkPage();
var
  I, Top: Integer;
begin
  Top := NetworkPage.PromptLabels[0].Top;
  for I := 0 to 4 do
  begin
    NetworkPage.PromptLabels[I].Top := Top;
    Top := Top + NetworkPage.PromptLabels[I].Height + ScaleY(2);
    NetworkPage.Edits[I].Top := Top;
    // Gap between an edit and the next prompt, small enough that the picker still lands inside
    // the surface if the edits turn out slightly taller. The clamp below is a backstop.
    Top := Top + NetworkPage.Edits[I].Height + ScaleY(6);
  end;

  CertCombo.Top := Top;
  // Backstop: whatever the reflow computes, the picker has to end up inside the surface.
  if CertCombo.Top + CertCombo.Height > NetworkPage.SurfaceHeight then
    CertCombo.Top := NetworkPage.SurfaceHeight - CertCombo.Height;
end;

procedure CertComboChange(Sender: TObject);
begin
  // Entry 0 is the prompt and maps to an empty thumbprint, so selecting it does not wipe a
  // thumbprint typed by hand.
  if (CertCombo.ItemIndex > 0) and (CertCombo.ItemIndex < GetArrayLength(CertThumbprints)) then
    NetworkPage.Values[4] := CertThumbprints[CertCombo.ItemIndex];
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
  Current := Trim(NetworkPage.Values[4]);
  if Current <> '' then
    for I := 1 to Added - 1 do
      if CompareText(CertThumbprints[I], Current) = 0 then
        CertCombo.ItemIndex := I;
end;

procedure CreateReadinessPage();
var
  I: Integer;
begin
  ReadinessPage := CreateCustomPage(ContentPage.ID, 'Prerequisites',
    'NodePilot checks what it needs before anything is changed.');

  for I := 0 to CheckCount - 1 do
  begin
    // Text first, glyph in a fixed column at the right edge, so the glyphs line up into a
    // status column instead of trailing ragged text.
    CheckLabels[I] := TNewStaticText.Create(ReadinessPage);
    CheckLabels[I].Parent := ReadinessPage.Surface;
    CheckLabels[I].Left := 0;
    CheckLabels[I].Width := ReadinessPage.SurfaceWidth - ScaleX(20);
    // Wrap rather than truncate: several details are long enough to be cut mid-sentence at this
    // width.
    CheckLabels[I].WordWrap := True;
    CheckLabels[I].AutoSize := True;
    CheckLabels[I].Cursor := crHand;
    CheckLabels[I].OnClick := @CheckLabelClick;
    CheckLabels[I].Caption := '';

    CheckMarks[I] := TNewStaticText.Create(ReadinessPage);
    CheckMarks[I].Parent := ReadinessPage.Surface;
    CheckMarks[I].Left := ReadinessPage.SurfaceWidth - ScaleX(16);
    CheckMarks[I].Width := ScaleX(16);
    CheckMarks[I].Font.Style := [fsBold];
    CheckMarks[I].Cursor := crHand;
    CheckMarks[I].OnClick := @CheckLabelClick;
    CheckMarks[I].Caption := '';

    CheckFixes[I] := TNewCheckBox.Create(ReadinessPage);
    CheckFixes[I].Parent := ReadinessPage.Surface;
    CheckFixes[I].Left := ScaleX(12);
    CheckFixes[I].Width := ReadinessPage.SurfaceWidth - ScaleX(12);
    CheckFixes[I].Height := ScaleY(17);
    CheckFixes[I].Visible := False;
  end;

  // Read-only memo holding the remediation text for the selected check. A memo keeps the text
  // selectable, so it can be copied out as well as saved with the button below.
  RemediationBox := TNewMemo.Create(ReadinessPage);
  RemediationBox.Parent := ReadinessPage.Surface;
  RemediationBox.Left := 0;
  RemediationBox.Width := ReadinessPage.SurfaceWidth;
  RemediationBox.ReadOnly := True;
  RemediationBox.WordWrap := True;
  // Scrollable because the content is unbounded: a database remediation is a multi-statement
  // CREATE LOGIN / CREATE USER / ALTER ROLE block and the check rows above leave it few lines.
  RemediationBox.ScrollBars := ssVertical;
  RemediationBox.Text := '';

  RecheckButton := TNewButton.Create(ReadinessPage);
  RecheckButton.Parent := ReadinessPage.Surface;
  RecheckButton.Top := ReadinessPage.SurfaceHeight - ScaleY(24);
  RecheckButton.Width := ScaleX(90);
  RecheckButton.Height := ScaleY(23);
  RecheckButton.Caption := 'Check again';
  RecheckButton.OnClick := @RecheckClick;

  SaveButton := TNewButton.Create(ReadinessPage);
  SaveButton.Parent := ReadinessPage.Surface;
  SaveButton.Top := RecheckButton.Top;
  SaveButton.Left := ScaleX(96);
  SaveButton.Width := ScaleX(120);
  SaveButton.Height := ScaleY(23);
  SaveButton.Caption := 'Save instructions...';
  SaveButton.OnClick := @SaveRemediationClick;
end;

// ---------------------------------------------------------------------------
// Wizard construction
// ---------------------------------------------------------------------------

procedure InitializeWizard();
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

  IdentityPage := CreateInputOptionPage(wpSelectDir,
    'Service identity',
    'Which account should the NodePilot service run as?',
    'The account also authenticates to SQL Server and to WinRM targets.', True, False);
  IdentityPage.Add('LocalSystem - authenticates on the network as this computer''s account');
  IdentityPage.Add('Group managed service account (gMSA)');
  IdentityPage.SelectedValueIndex := 0;

  AccountPage := CreateInputQueryPage(IdentityPage.ID,
    'Group managed service account',
    'Which gMSA should the service use?',
    'The account must already exist in Active Directory and be installed on this host. This setup does not create it.');
  AccountPage.Add('Account (DOMAIN\name$):', False);

  ProviderPage := CreateInputOptionPage(AccountPage.ID,
    'Database',
    'Which database will NodePilot use?',
    'The database itself is not part of this installer.', True, False);
  ProviderPage.Add('Microsoft SQL Server 2022 CU1 or newer');
  ProviderPage.Add('PostgreSQL 16 or newer');
  ProviderPage.SelectedValueIndex := 0;

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

  // Anchored to the last page created, not because it belongs to it.
  NetworkPage := CreateInputQueryPage(PostgresAuthPage.ID,
    'Network and TLS',
    'How will clients reach NodePilot?',
    'Kestrel terminates TLS itself using a certificate from the local machine store. There is no IIS and no reverse proxy.');
  NetworkPage.Add('Public host name:', False);
  NetworkPage.Add('HTTPS port:', False);
  NetworkPage.Add('HTTP port (0 disables the redirect):', False);
  NetworkPage.Add('Allowed host names (semicolon separated):', False);
  NetworkPage.Add('Certificate thumbprint:', False);
  NetworkPage.Values[1] := '443';
  NetworkPage.Values[2] := '80';

  // A sixth NetworkPage.Add() would be laid out below the visible surface of an input page, so the
  // picker is a control of its own placed under the last edit. It does nothing but fill that edit:
  // the edit stays the single value the rest of the wizard reads, which is why neither the answer
  // file, the validation nor the self-signed write-back had to learn that this exists.
  CertCombo := TNewComboBox.Create(NetworkPage);
  CertCombo.Parent := NetworkPage.Surface;
  CertCombo.Left := NetworkPage.Edits[4].Left;
  CertCombo.Width := NetworkPage.Edits[4].Width;
  // Top is set by CompactNetworkPage below - one place decides the vertical layout of this page.
  // Pick-only: free text belongs in the edit above, and two boxes accepting the same value are two
  // boxes that can disagree about it.
  CertCombo.Style := csDropDownList;
  CertCombo.OnChange := @CertComboChange;
  // Last, because it measures the controls above to place this one.
  CompactNetworkPage();

  // Checkboxes, not radio buttons: the third argument to CreateInputOptionPage being False is
  // what makes them independent, and this page is expected to grow another option one day.
  ContentPage := CreateInputOptionPage(NetworkPage.ID,
    'Optional content',
    'What should be installed alongside the product?',
    'These affect what is placed on this machine, not how NodePilot runs.', False, False);
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

  // Created here because Inno only accepts pages during InitializeWizard; shown much later, from
  // PrepareToInstall, which is where the installation actually runs.
  ProgressPage := CreateOutputProgressPage('Installing NodePilot',
    'Setup is installing NodePilot on this computer. This takes a few minutes.');
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
              (PageID = NetworkPage.ID) or (PageID = ReadinessPage.ID);
    Exit;

  end;
  if PageID = ModePage.ID then
    Result := (not IsUpgrade) or ForceFullReinstall
  else if IsUpdateSelected() then
    // An update needs no answers: it swaps binaries and leaves the configuration alone.
    Result := (PageID = wpSelectDir) or (PageID = IdentityPage.ID) or (PageID = AccountPage.ID) or
              (PageID = ProviderPage.ID) or (PageID = SqlPage.ID) or (PageID = PostgresPage.ID) or
              (PageID = PostgresAuthPage.ID) or
              (PageID = NetworkPage.ID) or (PageID = ReadinessPage.ID)
  else if PageID = AccountPage.ID then
    Result := IsLocalSystemSelected()
  else if PageID = SqlPage.ID then
    Result := not IsSqlServerSelected()
  else if (PageID = PostgresPage.ID) or (PageID = PostgresAuthPage.ID) then
    Result := IsSqlServerSelected();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // Reloaded on every visit rather than cached once: an operator who leaves to import a missing
  // certificate expects to find it in the list on the way back, and the call costs about a second.
  if CurPageID = NetworkPage.ID then
    LoadCertificateList();

  if CurPageID = ReadinessPage.ID then
    RunProbe();

  if (CurPageID = wpFinished) and (FinishSummary <> '') then
  begin
    // Two short sentences. The previous four-line paragraph wrapped far enough down the page that
    // its last line ran underneath the memo, which sits at a fixed offset below the label's top.
    WizardForm.FinishedLabel.Caption :=
      'NodePilot is installed and running.' + #13#10 +
      'Save the values below - the credentials are shown only here.';
    // Measured off the label instead of assuming its height: the caption above is short today, and
    // a translation or an extra sentence would silently push it back under the memo.
    FinishMemo.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
    FinishMemo.Height := WizardForm.FinishedPage.Height - FinishMemo.Top - ScaleY(38);
    FinishMemo.Text := FinishSummary;
    FinishMemo.Visible := True;
    FinishSaveButton.Top := FinishMemo.Top + FinishMemo.Height + ScaleY(8);
    FinishSaveButton.Visible := True;
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
  ProvisionIni, DbStatus: String;
  I: Integer;
  WantsFix: Boolean;
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
    else if not ValidatePort(NetworkPage.Values[1], False, 'The HTTPS port') then
      Result := False
    else if not ValidatePort(NetworkPage.Values[2], True, 'The HTTP port') then
      Result := False
    else
    begin
      Thumbprint := Trim(NetworkPage.Values[4]);
      StringChangeEx(Thumbprint, ' ', '', True);
      NetworkPage.Values[4] := Uppercase(Thumbprint);
      // Empty means no certificate is available yet. The prerequisite page validates the store.
      if (NetworkPage.Values[4] <> '') and (Length(NetworkPage.Values[4]) <> 40) then
      begin
        MsgBox('A certificate thumbprint is 40 hexadecimal characters. Leave the field empty if ' +
               'you want the next page to offer to create a self-signed certificate for you.', mbError, MB_OK);
        Result := False;
      end;
    end;
    Exit;
  end;

  if CurPageID = ReadinessPage.ID then
  begin
    WantsFix := False;
    for I := 0 to CheckCount - 1 do
      if CheckFixes[I].Visible and CheckFixes[I].Checked then
        WantsFix := True;

    if WantsFix then
    begin
      if not IsSqlServerSelected() then EnsurePgClient();
      EnsureRuntimePayload();
      WriteAnswerFile('install', False);
      ProvisionIni := SessionDir + '\provision.ini';
      if not RunPowerShell('-Mode Provision -AnswerFile "' + AnswerFilePath() + '" -OutFile "' +
        ProvisionIni + '"', ResultCode) or (ResultCode <> 0) then
      begin
        MsgBox('The requested changes could not be applied. See ' +
               ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log.', mbCriticalError, MB_OK);
        Result := False;
        Exit;
      end;

      // A run that changes nothing exits 0 like any other, so without this the wizard would simply
      // re-probe to the same red line and the operator would be left with "I ticked it, I pressed
      // Next, nothing happened" - which is exactly how the index bug above stayed invisible.
      DbStatus := GetIniString('provision.database', 'status', '', ProvisionIni);
      if (DbStatus <> '') and (DbStatus <> 'Pass') then
        // Kept on one continuation line: a line that STARTS with '#' is read by the preprocessor as
        // a directive, so '#13#10' may never begin one.
        MsgBox('The database could not be prepared:' + #13#10#13#10 +
               ExpandNewlines(GetIniString('provision.database', 'detail', '', ProvisionIni)) + #13#10#13#10 +
               'Select the database line below for the statements to hand to a DBA.', mbError, MB_OK)
      else if StrToIntDef(GetIniString('summary', 'actionsPerformed', '0', ProvisionIni), 0) = 0 then
        MsgBox('Nothing was changed - none of the ticked items produced an action.' + #13#10#13#10 +
               'See ' + ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log.', mbError, MB_OK);

      // A generated certificate produces a thumbprint the operator never typed.
      Thumbprint := GetIniString('provision.certificate', 'thumbprint', '', ProvisionIni);
      if Thumbprint <> '' then
        NetworkPage.Values[4] := Thumbprint;

      // Every tick is spent, whether or not it worked. Without this a fix that keeps failing -
      // no permission on the SQL Server is the realistic one - stays ticked, so Next runs it
      // again and returns to this page again, and the only way off is to notice the box and
      // clear it. The probe below is what says whether it took.
      for I := 0 to CheckCount - 1 do
        CheckFixes[I].Checked := False;

      // Never assume a fix worked.
      RunProbe();
      Result := False;
      Exit;
    end;

    if ProbeBlocking then
    begin
      MsgBox('At least one requirement is not met. Fix the items shown in red and choose ' +
             '"Check again".' + #13#10#13#10 + 'Continuing would fail during installation - ' +
             'Install-NodePilot.ps1 enforces the same checks.', mbError, MB_OK);
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
function RunAdapterWithProgress(const Arguments, ResultIni, ProgressPath: String;
  var ExitCode: Integer): String;
var
  Elapsed, SeenLines, Position, Shown: Integer;
  Lines: TArrayOfString;
  Line, Reported: String;
begin
  Result := '';
  ExitCode := -1;
  DeleteFile(ResultIni);
  DeleteFile(ProgressPath);

  if not StartPowerShell(Arguments) then
  begin
    Result := 'Could not start PowerShell to run the NodePilot installer.';
    Exit;
  end;

  SeenLines := 0;
  Elapsed := 0;
  Shown := 0;
  repeat
    // Called on EVERY tick, not only when the position changed. Inno's Pascal exposes no message
    // pump of its own - AppProcessMessages, ProcessMessages and Application are all unknown
    // identifiers here, verified against 6.7.3 - and TOutputProgressWizardPage is the mechanism
    // the tool provides for exactly this situation. Without something in the loop touching the
    // window, the frozen box this replaced comes straight back.
    if not WizardSilent() then
    begin
      ProgressPage.SetProgress(Shown, 100);
      WizardForm.Refresh();
    end;
    Sleep(AdapterPollMs);
    Elapsed := Elapsed + AdapterPollMs;

    // A failed read is a tick to skip, not an error: the adapter appends to this file while we
    // read it, so losing the race occasionally is expected.
    if LoadStringsFromFile(ProgressPath, Lines) then
    begin
      if GetArrayLength(Lines) > SeenLines then
      begin
        SeenLines := GetArrayLength(Lines);
        Line := StripBom(Lines[SeenLines - 1]);
        Position := StrToIntDef(PopField(Line), -1);
        // Never backwards. The adapter's phase table only ascends, but a bar that can retreat is
        // a bar nobody trusts, and the cost of forbidding it is one comparison.
        if Position > Shown then Shown := Position;
        if (not WizardSilent()) and (Line <> '') then ProgressPage.SetText(Line, '');
      end;
    end;

    // Existence alone is not enough - WriteAllLines is not atomic, so the file can be there and
    // still be half-written. The exit code is the last thing the adapter puts in it.
    if FileExists(ResultIni) then
    begin
      Reported := GetIniString('summary', 'exitCode', '', ResultIni);
      if Reported <> '' then
      begin
        ExitCode := StrToIntDef(Reported, 1);
        Exit;
      end;
    end;
  until Elapsed >= AdapterTimeoutMs;

  Result := 'The NodePilot installer did not finish within ' +
    IntToStr(AdapterTimeoutMs div 60000) + ' minutes and was given up on. ' +
    'The installation may still be running; check the log before starting over.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AnswerMode, Arguments, ResultIni, ProvisionIni, DbStatus, Extra: String;
begin
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

  if not WizardSilent() then
  begin
    ProgressPage.SetProgress(0, 100);
    ProgressPage.SetText('Starting the NodePilot installer', '');
    ProgressPage.Show();
  end;
  try
    Result := RunAdapterWithProgress(Arguments, ResultIni, SessionDir + '\progress.txt', ResultCode);
  finally
    if not WizardSilent() then ProgressPage.Hide();
  end;
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
// It never touches the DATABASE, and there is no option to. This installer does not create the
// database; it is provisioned separately, it may be replicated, backed up or shared with
// something else, and removing what you never installed is not a decision an uninstaller gets to
// make. The wizard says so rather than staying quiet about it, because "did that just delete my
// data?" is the question an operator will otherwise be left with.
//
// The data directory IS ours - logs, the JWT signing key, the data-protection keyring - so that
// one is a genuine choice, defaulting to KEEP everywhere: interactive, /SILENT with no switches,
// Apps & Features, and Inno's own [UninstallRun]. An uninstall that destroys data because nobody
// said otherwise is not a defensible default, and the unattended path is exactly where nobody is
// watching.
//
// SuppressibleTaskDialogMsgBox is what makes "also usable silently" work without a second code
// path: interactive it asks, and under /SUPPRESSMSGBOXES it returns the default given here.

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
    'Your DATABASE is not affected either way. This installer did not create it and will not' + #13#10 +
    'remove it - drop it yourself once you are certain nothing else uses it.',
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
  if SessionDir <> '' then
    RunPowerShell('-Mode Cleanup -SessionPath "' + SessionDir + '" -AnswerFile "' +
      AnswerFilePath() + '"', ResultCode);
end;
