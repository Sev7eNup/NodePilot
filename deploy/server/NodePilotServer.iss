; NodePilot server setup (Inno Setup 6).
;
; A thin wizard in front of Install-NodePilot.ps1 - pages and payload, no installation logic.
; Everything it collects goes into an ACL-protected JSON answer file which
; deploy\Invoke-NodePilotSetup.ps1 reads and splats into the existing deployment scripts. That
; indirection is not decoration: -PostgresPassword is a [SecureString] and cannot be passed on a
; powershell.exe command line at all, and it gives /SILENT /ANSWERFILE= for SCCM for free.
;
; Deliberately NO [Run] section. [Run] cannot inspect an exit code, and the desktop installer's
; equivalent silently swallows a failed provisioning run. Everything here goes through Exec() in
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
; Windows Server 2022 is build 20348. The desktop installer pins 22000 because it targets
; Windows 11 - copying that here would make the SERVER installer refuse to run on the only
; operating system it actually targets.
MinVersion=10.0.20348
OutputDir={#OutputDir}
OutputBaseFilename=NodePilot-Server-Setup-{#AppVersion}
WizardStyle=modern
SetupIconFile={#StageDir}\setup-icon.ico
LicenseFile={#StageDir}\LICENSE.txt
; A failed setup has to be diagnosable without asking the operator to reproduce it.
SetupLogging=yes

[Files]
; Everything setup needs AT RUNTIME is dontcopy, extracted to {tmp} on demand, because every
; phase that uses it runs before Inno has copied a single file: the readiness page during the
; wizard, and the installation itself from PrepareToInstall.
;
; The installation cannot live in ssPostInstall, which is the obvious place for it. Measured on
; Inno 6.7.3: neither RaiseException nor Abort in ssPostInstall changes the exit code - a failed
; install still reports 0. Under SCCM that is a deployment that claims success and installed
; nothing. PrepareToInstall returns a message and exits 7, so failure is visible.
;
; payload\ and deploy\ are separate staging trees holding the same scripts twice. Inno
; deduplicates identical SOURCE files, so listing one file both dontcopy and with a DestDir
; collapses the pair into a single entry and the dontcopy variant silently vanishes - which
; showed up as the scripts being extracted into a literal "{app}\deploy" folder under {tmp}.
Source: "{#StageDir}\payload\*";    Flags: dontcopy

; The only thing that stays on disk: the deployment scripts, for the uninstaller wired up below
; and for an operator who later wants to run Update-NodePilot.ps1 by hand. Copied after
; PrepareToInstall, which is the right order - Install-NodePilot.ps1 wipes its install directory
; before repopulating it, so anything placed there first would be deleted again.
Source: "{#StageDir}\deploy\*";     DestDir: "{app}\deploy"; Flags: recursesubdirs createallsubdirs ignoreversion

[UninstallDelete]
; Inno decides whether {app} is empty BEFORE it removes its own uninstaller from inside it, so
; without this the uninstall finishes leaving an empty C:\Program Files\NodePilot behind. This
; entry runs last and clears it. Observed on the lab host; an empty folder is a small thing, but a
; leftover folder is what makes an operator wonder what else was left.
Type: dirifempty; Name: "{app}"

; Deliberately NO [UninstallRun] section, for the same two reasons [Run] is absent above.
;
; It cannot inspect an exit code - and, measured on the lab host, it cannot carry an uninstall-time
; decision either: Inno evaluates {code:...} in [UninstallRun] parameters at INSTALL time and
; records the resulting string in unins000.dat. A /PURGEDATA=1 given to the uninstaller reached the
; uninstaller's command line correctly and still never reached the script, because the argument
; string had been frozen weeks earlier. The uninstall is invoked from [Code] instead.

[Code]
const
  ExitProbeFailed = 2;
  ExitAnswerFileInvalid = 3;
  ExitInstallFailed = 4;
  CheckCount = 9;

  // Status glyphs. Written as character codes so this file stays pure ASCII on disk - a .iss
  // that needs a specific encoding to compile is a trap for the next editor.
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
  ReadinessPage: TWizardPage;

  CheckIds: array[0..CheckCount - 1] of String;
  CheckLabels: array[0..CheckCount - 1] of TNewStaticText;
  CheckMarks: array[0..CheckCount - 1] of TNewStaticText;
  CheckFixes: array[0..CheckCount - 1] of TNewCheckBox;
  RemediationLabel: TNewStaticText;
  RemediationText: String;
  RecheckButton: TNewButton;
  SaveButton: TNewButton;

  // Certificate picker on the TLS page. The thumbprint of a certificate already installed on the
  // machine is otherwise only reachable through the certificate MMC, whose copy button prepends an
  // invisible U+200E - which is why the installer strips non-hex characters before measuring the
  // length. Picking one here skips that trip entirely.
  CertCombo: TNewComboBox;
  CertThumbprints: array of String;

  // Finish page. The values here exist nowhere else the operator can reach: the API key is
  // generated by the adapter and printed to a console that does not exist under a hidden Exec,
  // and install-report.txt omits it by design.
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

// Always the {tmp} copy, never {app}\deploy: the wizard's readiness page runs before any file has
// been installed, and using two different locations depending on the phase is how one of them
// rots. {app}\deploy\ still ships, for the uninstaller and for later manual use.
function AdapterPath(): String;
begin
  Result := ExpandConstant('{tmp}\Invoke-NodePilotSetup.ps1');
end;

function RunPowerShell(const Arguments: String; var ResultCode: Integer): Boolean;
begin
  // -PayloadRoot passed explicitly rather than derived from the script's own location. It happens
  // to be the same directory today, but "the payload is wherever this script happens to sit" is
  // the kind of assumption that survives right up until someone moves one of them.
  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + AdapterPath() + '"' +
    ' -PayloadRoot "' + ExpandConstant('{tmp}') + '" ' + Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// The adapter writes the session path with Set-Content -Encoding UTF8, which on Windows
// PowerShell 5.1 means a byte-order mark. LoadStringFromFile hands back raw bytes as an
// AnsiString, so the BOM arrives as three leading characters and Trim() does not touch them -
// producing a path that looks right in a log and resolves to nothing. Stripped on both sides.
function StripBom(const Value: String): String;
begin
  Result := Value;
  if (Length(Result) >= 3) and (Ord(Result[1]) = 239) and (Ord(Result[2]) = 187) and (Ord(Result[3]) = 191) then
    Result := Copy(Result, 4, Length(Result) - 3);
  if (Length(Result) >= 1) and (Ord(Result[1]) = 65279) then
    Result := Copy(Result, 2, Length(Result) - 1);
  Result := Trim(Result);
end;

// Escapes a string for embedding in JSON. The whole Pascal-side JSON surface is this one
// function, which is why the answer file is written here and parsed - strictly - on the
// PowerShell side.
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

// Locates the uninstaller this setup family registered. Empty when the installation came from
// the zip package: that path leaves the HKLM marker DetectExistingInstallation reads, but no
// unins000.exe, and offering a button that cannot work is worse than saying so.
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

// Built as a line array rather than one concatenated string so it can be written with
// SaveStringsToUTF8File. This Inno version has no SaveStringToUTF8File, and the AnsiString-based
// SaveStringToFile would encode a password or host name containing non-ASCII characters in the
// system codepage - which the adapter, reading UTF-8, would then reject or mangle.
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
  if ForProbe then
    AddLine(Lines, Count, '  }')
  else
  begin
    AddLine(Lines, Count, '  },');
    AddLine(Lines, Count, '  "provisioning": {');
    AddLine(Lines, Count, '    "installDotnetRuntime": ' + JsonBool(CheckFixes[0].Checked) + ',');
    AddLine(Lines, Count, '    "generateSelfSignedCertificate": ' + JsonBool(CheckFixes[1].Checked) + ',');
    AddLine(Lines, Count, '    "createDatabaseAndLogin": ' + JsonBool(CheckFixes[5].Checked) + ',');
    AddLine(Lines, Count, '    "trustArtifactSigner": false');
    AddLine(Lines, Count, '  }');
  end;
  AddLine(Lines, Count, '}');

  SetArrayLength(Lines, Count);
  Result := Lines;
end;

procedure WriteAnswerFile(const AnswerMode: String; const ForProbe: Boolean);
begin
  // /ANSWERFILE wins over the pages. This is the unattended path - SCCM, GPO, a golden image -
  // and it is why the answer file exists as a file rather than as a command line in the first
  // place: -PostgresPassword is a [SecureString] and cannot be passed as an argument at all.
  //
  // The supplied file is COPIED into the session directory rather than used where it lies, so it
  // inherits that directory's restrictive DACL and gets shredded with it. The operator's original
  // is left alone; managing that one is their business.
  if AnswerFileOverride <> '' then
  begin
    if not FileCopy(AnswerFileOverride, AnswerFilePath(), False) then
      RaiseException('Could not read the answer file: ' + AnswerFileOverride);
    Exit;
  end;

  // Written into the session directory, whose DACL is SYSTEM + Administrators + the installing
  // user, applied atomically when the adapter created it. The file inherits that, which is why
  // nothing here does ACL work.
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
  RemediationLabel.Caption := RemediationText;
end;

procedure CheckLabelClick(Sender: TObject);
var
  I: Integer;
begin
  // Either half of a row is a valid click target - the glyph sits in its own control, and
  // hitting it should do what hitting the text does.
  for I := 0 to CheckCount - 1 do
    if (CheckLabels[I] = Sender) or (CheckMarks[I] = Sender) then
      UpdateRemediation(I);
end;

// Rows are placed here rather than at construction time because a hidden auto-fix checkbox used
// to reserve its 16 px anyway: eight of them ate 128 px of a 309 px surface for controls that are
// almost never shown, which is what squeezed the remediation area down to a single line.
procedure LayoutReadiness();
var
  I, Y, ButtonTop, Available: Integer;
begin
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
      CheckFixes[I].Top := Y;
      Y := Y + ScaleY(19);
    end;
  end;

  ButtonTop := ReadinessPage.SurfaceHeight - ScaleY(24);
  RemediationLabel.Top := Y + ScaleY(8);
  Available := ButtonTop - ScaleY(6) - RemediationLabel.Top;
  // Never negative, never overlapping the buttons: with every row wrapped and every fix offered
  // there is little left, and a label with no room is still better than one drawn over them.
  if Available < ScaleY(13) then Available := ScaleY(13);
  RemediationLabel.Height := Available;
end;

// Created lazily, because both the readiness page and PrepareToInstall need it and either can be
// the first to run.
function EnsureSession(): String;
var
  ResultCode: Integer;
  HandoffFile: String;
  // LoadStringFromFile hands back an AnsiString - this Inno version has no UTF-8 counterpart.
  // Safe only because the adapter puts the session under %ProgramData%, whose path is ASCII on
  // every system; %TEMP% contains the account name and would not be.
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

    // Colour alone carried the status until now, which says nothing to anyone who cannot
    // separate this green from this red - and nothing at all in a greyscale screenshot.
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

    // A fix is only offered for a red row that the adapter says it can act on. Ticking one and
    // clicking Next runs Provision and then re-runs this probe - the fix is never assumed to
    // have worked.
    CheckFixes[I].Visible := (Status = 'Fail') and
      (GetIniString('check.' + CheckIds[I], 'canAutoFix', '0', Ini) = '1') and
      (AutoFixLabel <> '');
    CheckFixes[I].Caption := AutoFixLabel;
    if not CheckFixes[I].Visible then
      CheckFixes[I].Checked := False;
  end;

  ProbeRan := True;
  ProbeBlocking := ResultCode = ExitProbeFailed;
  RemediationText := 'Select a line above to see what to do about it.';
  RemediationLabel.Caption := RemediationText;
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
  // Inno Setup's Pascal Script has no clipboard API and the text lives in a label rather than a
  // selectable memo, so this button is the only way the instructions leave the wizard.
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

// Everything an operator needs to reach the installation for the first time, assembled from the
// adapter's result file. Two of these values exist nowhere else they can get at: the
// External-Trigger API key is generated by the adapter and deliberately absent from
// install-report.txt, and the admin setup token is consumed by the first login.
procedure BuildFinishSummary(const ResultIni: String);
var
  Url, Token, ApiKey, Thumb, InstallDir, DataDir, Service, S: String;
begin
  Url := GetIniString('result', 'url', '', ResultIni);
  Token := GetIniString('result', 'adminSetupToken', '', ResultIni);
  ApiKey := GetIniString('result', 'externalTriggerApiKey', '', ResultIni);
  Thumb := GetIniString('result', 'certificateThumbprint', '', ResultIni);
  InstallDir := GetIniString('result', 'installPath', '', ResultIni);
  DataDir := GetIniString('result', 'dataPath', '', ResultIni);
  Service := GetIniString('result', 'serviceName', '', ResultIni);

  S := '';
  if Url <> '' then
    S := S + 'Open NodePilot at' + #13#10 + '    ' + Url + #13#10#13#10;

  if Token <> '' then
    S := S + 'First login - setup token (one-shot, needed only while no account exists):' + #13#10 +
             '    ' + Token + #13#10#13#10
  else if GetIniString('result', 'adminSetupTokenUnreadable', '', ResultIni) <> '' then
    S := S + 'First login - the setup token could not be read from' + #13#10 +
             '    ' + DataDir + '\admin-setup.token' + #13#10 +
             '  It is owner-only for the service account. Read it with: robocopy /B' + #13#10#13#10
  else if not IsUpdateSelected() then
    S := S + 'First login - no setup token was issued, so the database already has accounts.' + #13#10 +
             '  Sign in with an existing one.' + #13#10#13#10;

  if ApiKey <> '' then
    S := S + 'External-Trigger API key (header X-Api-Key). Copy it now - it is not' + #13#10 +
             'recoverable, and re-running this setup issues a different one:' + #13#10 +
             '    ' + ApiKey + #13#10#13#10;

  if Thumb <> '' then
    S := S + 'TLS certificate' + #13#10 + '    ' + Thumb + #13#10 +
             '  If it is self-signed, import it into LocalMachine\Root on every client' + #13#10 +
             '  or the browser will call the site unsafe.' + #13#10#13#10;

  if Service <> '' then S := S + 'Service        ' + Service + #13#10;
  if InstallDir <> '' then S := S + 'Program files  ' + InstallDir + #13#10;
  if DataDir <> '' then S := S + 'Data           ' + DataDir + #13#10;

  FinishSummary := S;
end;

// ---------------------------------------------------------------------------
// Certificate picker
// ---------------------------------------------------------------------------

// Pops the text up to the next '|' off Rest and returns it. Inno's Pascal has no split, and the
// line format is fixed at four fields by Format-NodePilotCertificateLine, which also guarantees no
// separator can appear inside the subject.
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

// Inno lays an input page out at 54 px per label+edit pair, which is generous: a prompt label is
// about 13 px tall and an edit about 21. Five pairs therefore claim 270 of the 309 px surface and
// leave the picker below the bottom edge - it shipped drawn as a sliver, because an input page
// does not scroll and gives no hint that anything is under the edge.
//
// Reflowing the same controls at the heights they actually have frees roughly 55 px, which is
// enough for the picker on the SAME page. Splitting the page would have been the other way out and
// a worse one: five values that belong to one decision, spread over two screens.
//
// Measured off the controls rather than off constants, so a font change or a sixth field moves it
// instead of quietly reintroducing the clipping.
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
    // 6 px between an edit and the next prompt. Chosen so the picker lands inside the surface even
    // if an edit turns out to be 23 px rather than 21 - the clamp below is a backstop, not the plan.
    Top := Top + NetworkPage.Edits[I].Height + ScaleY(6);
  end;

  CertCombo.Top := Top;
  // Last line of defence, and the reason this is not just arithmetic: whatever the reflow computes,
  // the picker has to end up inside the surface. Being a few pixels too low is invisible in the
  // source and unmistakable on screen.
  if CertCombo.Top + CertCombo.Height > NetworkPage.SurfaceHeight then
    CertCombo.Top := NetworkPage.SurfaceHeight - CertCombo.Height;
end;

procedure CertComboChange(Sender: TObject);
begin
  // Entry 0 is the prompt and maps to an empty thumbprint deliberately: landing back on it must
  // not wipe a thumbprint the operator typed by hand.
  if (CertCombo.ItemIndex > 0) and (CertCombo.ItemIndex < GetArrayLength(CertThumbprints)) then
    NetworkPage.Values[4] := CertThumbprints[CertCombo.ItemIndex];
end;

procedure LoadCertificateList();
var
  ResultCode, Count, Added, I: Integer;
  Ini, Rest, Thumbprint, Subject, HasKey, Expires, Entry, Current: String;
begin
  // Into {tmp} rather than the protected session directory, and not out of laziness: this runs on
  // the TLS page, which comes before anything has created a session, and thumbprints and subjects
  // out of a store every local user can read are not a secret to begin with.
  Ini := ExpandConstant('{tmp}\certificates.ini');
  DeleteFile(Ini);
  CertCombo.Items.Clear();
  SetArrayLength(CertThumbprints, 1);
  CertThumbprints[0] := '';
  WizardForm.Update();

  if not RunPowerShell('-Mode Certificates -OutFile "' + Ini + '"', ResultCode) or (ResultCode <> 0) then
  begin
    // Never blocking. The field above still takes a thumbprint typed by hand and the prerequisite
    // page checks it either way, so a picker that cannot be filled costs convenience and nothing
    // else. Reporting it as an error would stop an installation that is perfectly able to proceed.
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
    // A malformed line is dropped rather than offered: selecting it would put something that is
    // not a thumbprint into the field and fail the next page for a reason that is not the truth.
    if Length(Thumbprint) <> 40 then Continue;

    // The thumbprint is in the caption, not just behind it. On the lab host two certificates share
    // a subject AND an expiry date - "NodePilot Lab HTTPS" and "NodePilot Lab SQL TLS", issued 39
    // seconds apart - so subject and date rendered two identical lines, and picking the wrong one
    // would have configured Kestrel with the database's certificate without saying anything.
    // Showing the value that lands in the box above also means the operator can check it against a
    // thumbprint they were handed, instead of selecting on trust.
    Entry := Subject + '   ' + Thumbprint + '   expires ' + Expires;
    // Listed, not filtered out. "It is in the store, why is it not offered?" has one common
    // answer - a .cer was imported where a .pfx was meant - and hiding the certificate keeps that
    // a mystery until the prerequisite page says it about a thumbprint typed out by hand.
    if HasKey <> '1' then
      Entry := Entry + '   NO PRIVATE KEY';
    CertCombo.Items.Add(Entry);
    CertThumbprints[Added] := Thumbprint;
    Added := Added + 1;
  end;
  SetArrayLength(CertThumbprints, Added);

  // Show what the field already holds, so returning to this page does not offer "pick one" over a
  // thumbprint that is already set - including the one a generated certificate wrote into the
  // field from the prerequisite page.
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
  ReadinessPage := CreateCustomPage(NetworkPage.ID, 'Prerequisites',
    'NodePilot checks what it needs before anything is changed.');

  for I := 0 to CheckCount - 1 do
  begin
    // Text first, glyph in a fixed column at the right edge, so the glyphs line up into a
    // status column instead of trailing ragged text.
    CheckLabels[I] := TNewStaticText.Create(ReadinessPage);
    CheckLabels[I].Parent := ReadinessPage.Surface;
    CheckLabels[I].Left := 0;
    CheckLabels[I].Width := ReadinessPage.SurfaceWidth - ScaleX(20);
    // Wrapping matters: several details are long enough to be cut mid-sentence at this width,
    // and a check that reports half a sentence is worse than one that takes two lines.
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

  // A read-only memo sized to the leftovers ended up one line tall with a scrollbar, which reads
  // as a broken edit field. A wrapped label carries the same text and cannot be mistaken for
  // something to type into. The cost is that the text is no longer selectable, so "Save
  // instructions..." is now the only way to get it out of the wizard - it stays for that reason.
  RemediationLabel := TNewStaticText.Create(ReadinessPage);
  RemediationLabel.Parent := ReadinessPage.Surface;
  RemediationLabel.Left := 0;
  RemediationLabel.Width := ReadinessPage.SurfaceWidth;
  RemediationLabel.WordWrap := True;
  RemediationLabel.AutoSize := False;
  RemediationLabel.Caption := '';

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

  // Before anything can call the adapter, and that is every phase of this wizard.
  ExtractTemporaryFiles('*.ps1');

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
  // Removal belongs on the page an operator actually reaches. It is also reachable through
  // Apps & Features, but nobody who just double-clicked the setup goes looking there.
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

  // Each page is anchored to the one created BEFORE it, never to a shared parent. Inno inserts a
  // page directly after the ID it is given, so two pages anchored to the same parent come out in
  // reverse creation order - and anything anchored to the earlier of the two then lands in front
  // of the later one. Anchoring SqlPage and PostgresPage both to ProviderPage produced
  // Provider -> Postgres -> Network -> Prerequisites -> Sql: the SQL page sat AFTER the page that
  // reads its values, so it was never shown and its fields stayed at their defaults.
  SqlPage := CreateInputQueryPage(ProviderPage.ID,
    'SQL Server',
    'Where does NodePilot find its database?',
    'The connection is encrypted and the server certificate is verified, so the name below must match that certificate.');
  SqlPage.Add('Server (host, host\instance or host,port):', False);
  SqlPage.Add('Database:', False);
  SqlPage.Add('Certificate host name (leave blank to derive it):', False);
  SqlPage.Values[1] := 'NodePilot';

  // Split across two pages, and not for cosmetic reasons: an Inno input page has 309 pixels of
  // surface and each label+edit pair costs 54, so the sixth field lands at 337 and is simply not
  // drawn. Measured. The page does not scroll and gives no hint that anything is missing - the
  // root-certificate field was invisible, and the wizard then failed on a value the operator was
  // never given the chance to enter. Five is the maximum; every page here stays at three.
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
  FinishMemo.ScrollBars := ssVertical;
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
    WizardForm.FinishedLabel.Caption :=
      'NodePilot is installed and the service is running.' + #13#10#13#10 +
      'Everything you need for the first connection is below. The setup token and the API key ' +
      'are shown here once - save them before you close this window.';
    FinishMemo.Text := FinishSummary;
    FinishMemo.Visible := True;
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
      if Length(NetworkPage.Values[4]) <> 40 then
      begin
        MsgBox('A certificate thumbprint is 40 hexadecimal characters. Leave it as is for now if ' +
               'you want the next page to create a self-signed certificate for you.', mbError, MB_OK);
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
      WriteAnswerFile('install', False);
      if not RunPowerShell('-Mode Provision -AnswerFile "' + AnswerFilePath() + '" -OutFile "' +
        SessionDir + '\provision.ini"', ResultCode) or (ResultCode <> 0) then
      begin
        MsgBox('The requested changes could not be applied. See ' +
               ExpandConstant('{%TEMP}') + '\nodepilot-server-setup.log.', mbCriticalError, MB_OK);
        Result := False;
        Exit;
      end;
      // A generated certificate produces a thumbprint the operator never typed.
      Thumbprint := GetIniString('provision.certificate', 'thumbprint', '', SessionDir + '\provision.ini');
      if Thumbprint <> '' then
        NetworkPage.Values[4] := Thumbprint;
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
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  AnswerMode, Arguments, ResultIni, Extra: String;
begin
  Result := EnsureSession();
  if Result <> '' then Exit;

  ExtractTemporaryFile('{#ArtifactFileName}');
  ExtractTemporaryFile('{#ArtifactFileName}.manifest.json');
  ExtractTemporaryFile('{#ArtifactFileName}.manifest.json.p7s');
  ExtractTemporaryFile('nodepilot-release-signing.cer');
  ExtractTemporaryFile('{#RuntimeFileName}');

  if IsUpdateSelected() then AnswerMode := 'update' else AnswerMode := 'install';
  WriteAnswerFile(AnswerMode, False);
  ResultIni := SessionDir + '\result.ini';

  // -Mode Apply, not Install or Update: the answer file already declares which it is, and a
  // second place to say so is a second place for them to disagree. That matters most with
  // /ANSWERFILE, where the file comes from outside the wizard entirely.
  Arguments := '-Mode Apply' +
    ' -AnswerFile "' + AnswerFilePath() + '"' +
    ' -ArtifactPath "' + ExpandConstant('{tmp}\{#ArtifactFileName}') + '"' +
    ' -TrustedArtifactSignerThumbprint "{#SignerThumbprint}"' +
    ' -OutFile "' + ResultIni + '"';

  if not WizardSilent() then
  begin
    WizardForm.StatusLabel.Caption := 'Running the NodePilot installer. This can take a few minutes.';
    WizardForm.Update();
  end;

  if not RunPowerShell(Arguments, ResultCode) then
  begin
    Result := 'Could not start PowerShell to run the NodePilot installer.';
    Exit;
  end;

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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ScriptPath, Arguments, Switches: String;
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

    Arguments := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"' +
      ' -ServiceName "' + GetServiceName('') + '"' +
      ' -InstallPath "' + ExpandConstant('{app}') + '"' + Switches;

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
