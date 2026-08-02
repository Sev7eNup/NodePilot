# DB-Belege fuer den Windows-SSO-Feldtest. Read-only -- das Skript schreibt nichts.
#
# AUSFUEHREN AUF: dem Host mit Postgres-Zugriff (npapi01) bzw. mit passendem -DbHost.
#
# Aufruf:
#   .\Assert-DbState.ps1 -DbPassword '<pw>'                    # alle Szenarien
#   .\Assert-DbState.ps1 -DbPassword '<pw>' -Scenario Ntlm     # nur ein Szenario
#
# psql IMMER mit -w aufrufen: ohne das haengt der Konsolen-Passwortprompt das Skript
# in nicht-interaktiven Shells auf.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Wegwerf-Lab-Credentials einer Testdatenbank. SecureString wuerde PGPASSWORD nicht sicherer machen (psql liest die Env-Var im Klartext) und den Copy-Paste-Ablauf des READMEs brechen.')]
param(
    [string]$PsqlPath = 'C:\NodePilot-Postgres\pgsql\bin\psql.exe',
    [string]$DbHost = '127.0.0.1',
    [string]$DbUser = 'nodepilot',
    [string]$DbName = 'nodepilot_adssotest',
    [Parameter(Mandatory = $true)]
    [string]$DbPassword,
    [ValidateSet('All', 'Identity', 'Gate', 'Race', 'Revocation', 'Ntlm', 'Audit')]
    [string]$Scenario = 'All'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PsqlPath)) { throw "psql nicht gefunden: $PsqlPath" }

$AdAuthority = 'urn:nodepilot:identity:active-directory'

function Invoke-Psql([string]$sql, [switch]$Table) {
    $env:PGPASSWORD = $DbPassword
    try {
        # NICHT $args nennen -- das ist eine automatische Variable.
        $psqlArgs = @('-w', '-h', $DbHost, '-U', $DbUser, '-d', $DbName)
        if (-not $Table) { $psqlArgs += @('-A', '-t') }
        # SQL ueber stdin (-f -), NICHT ueber -c: PowerShell verschluckt beim Aufruf einer
        # nativen exe die doppelten Anfuehrungszeichen im Argument. Aus "Users" wuerde
        # Users, Postgres faltet das auf lowercase und antwortet
        # 'relation "users" does not exist'. Fuer die PascalCase-Tabellen von EF sind die
        # Quotes zwingend, stdin umgeht das Argument-Quoting vollstaendig.
        $sql | & $PsqlPath @psqlArgs -f -
    } finally { $env:PGPASSWORD = $null }
}
function Get-Scalar([string]$sql) { ((Invoke-Psql $sql) -join '').Trim() }

$results = New-Object System.Collections.ArrayList
function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$results.Add([pscustomobject]@{ Check = $name; Ok = $ok; Detail = $detail })
}

$run = { param($s) $Scenario -eq 'All' -or $Scenario -eq $s }

# ---------- Identitaet: LDAP und Windows teilen einen Subject ----------
# Die Authority ist fuer beide Pfade der kanonische AD-Namensraum, der Subject die SID.
# Zwei Zeilen fuer denselben Menschen hiessen: die Pfade haben getrennte Konten angelegt.
if (& $run 'Identity') {
    "--- Identitaeten (Authority/Subject je User) ---"
    Invoke-Psql @"
SELECT u."Username", u."Role", u."IsActive", u."DirectorySyncStatus", e."Subject"
FROM "Users" u LEFT JOIN "ExternalIdentities" e ON e."UserId" = u."Id"
WHERE u."Username" ILIKE 'np.%' ORDER BY u."Username";
"@ -Table

    $dupes = Get-Scalar @"
SELECT COUNT(*) FROM (
  SELECT e."UserId" FROM "ExternalIdentities" e
  WHERE e."Authority" = '$AdAuthority'
  GROUP BY e."UserId" HAVING COUNT(*) > 1) x;
"@
    Add-Check 'W9. Kein User mit mehreren AD-Identitaeten' ($dupes -eq '0') "User mit >1 AD-Identity: $dupes"

    $alice = Get-Scalar "SELECT COUNT(*) FROM ""Users"" WHERE ""Username"" ILIKE 'np.alice%';"
    Add-Check 'W9. Genau ein alice-User (LDAP + SSO)' ($alice -eq '1') "COUNT=$alice"

    # Gruppen leben serverseitig, nicht im Token. alice erbt die Admission transitiv.
    "--- DirectoryMemberships alice ---"
    Invoke-Psql @"
SELECT m."Authority", m."GroupKey" FROM "DirectoryMemberships" m
JOIN "Users" u ON u."Id" = m."UserId" WHERE u."Username" ILIKE 'np.alice%';
"@ -Table
}

# ---------- Gate: bob darf keinen JIT-Row erzeugen ----------
# Die Admission ist fail-closed: ohne Treffer in AllowedGroupSids entsteht KEIN Konto.
# Ein angelegter, aber rollenloser User waere eine stille Regression.
if (& $run 'Gate') {
    $bobUsers = Get-Scalar "SELECT COUNT(*) FROM ""Users"" WHERE ""Username"" ILIKE 'np.bob%';"
    Add-Check 'W12. Kein User-Row fuer bob' ($bobUsers -eq '0') "COUNT=$bobUsers"
    $bobIds = Get-Scalar @"
SELECT COUNT(*) FROM "ExternalIdentities" e
JOIN "Users" u ON u."Id" = e."UserId" WHERE u."Username" ILIKE 'np.bob%';
"@
    Add-Check 'W12. Keine ExternalIdentity fuer bob' ($bobIds -eq '0') "COUNT=$bobIds"
    $refused = Get-Scalar @"
SELECT COUNT(*) FROM "AuditLog"
WHERE "Action" = 'USER_DIRECTORY_ACCESS_REFUSED' AND "Timestamp" > NOW() - INTERVAL '2 hours';
"@
    Add-Check 'W12. Audit USER_DIRECTORY_ACCESS_REFUSED vorhanden' ([int]$refused -gt 0) "COUNT=$refused"
}

# ---------- Race: parallele Erst-Logins ----------
if (& $run 'Race') {
    $daveUsers = Get-Scalar "SELECT COUNT(*) FROM ""Users"" WHERE ""Username"" ILIKE 'np.dave%';"
    Add-Check 'W18. Genau ein User fuer dave' ($daveUsers -eq '1') "COUNT=$daveUsers"
    $daveIds = Get-Scalar @"
SELECT COUNT(*) FROM "ExternalIdentities" e
JOIN "Users" u ON u."Id" = e."UserId" WHERE u."Username" ILIKE 'np.dave%';
"@
    Add-Check 'W18. Genau eine Identity fuer dave' ($daveIds -eq '1') "COUNT=$daveIds"
    # Genau EIN JIT_CREATED beweist, dass die Unique-Index-Race korrekt aufgeloest wurde
    # (die Verlierer laufen in den Update-Pfad, nicht in einen zweiten Insert).
    $created = Get-Scalar @"
SELECT COUNT(*) FROM "AuditLog"
WHERE "Action" = 'USER_WINDOWS_JIT_CREATED' AND "Username" ILIKE 'np.dave%';
"@
    Add-Check 'W18. Genau ein USER_WINDOWS_JIT_CREATED fuer dave' ($created -eq '1') "COUNT=$created"
}

# ---------- Revocation: Gruppenentzug / Account-Disable ----------
if (& $run 'Revocation') {
    "--- erin: Status + Sessions ---"
    Invoke-Psql @"
SELECT u."Username", u."IsActive", u."IsTombstoned", u."DirectorySyncStatus",
       (SELECT COUNT(*) FROM "AuthSessions" s WHERE s."UserId" = u."Id" AND s."RevokedAt" IS NULL) AS "AktiveSessions"
FROM "Users" u WHERE u."Username" ILIKE 'np.erin%';
"@ -Table

    $open = Get-Scalar @"
SELECT COUNT(*) FROM "AuthSessions" s JOIN "Users" u ON u."Id" = s."UserId"
WHERE u."Username" ILIKE 'np.erin%' AND s."RevokedAt" IS NULL;
"@
    Add-Check 'W15. Keine offene Session fuer erin' ($open -eq '0') "offeneSessions=$open"
    $deprov = Get-Scalar @"
SELECT COUNT(*) FROM "AuditLog"
WHERE "Action" = 'USER_DIRECTORY_DEPROVISIONED' AND "Timestamp" > NOW() - INTERVAL '2 hours';
"@
    Add-Check 'W15. Audit USER_DIRECTORY_DEPROVISIONED vorhanden' ([int]$deprov -gt 0) "COUNT=$deprov"

    # Gegenprobe zu W17: der Sync darf NIE die ganze Mandantschaft tombstonen.
    $tomb = Get-Scalar "SELECT COUNT(*) FROM ""Users"" WHERE ""IsTombstoned"" = true;"
    Add-Check 'W17. Kein Massen-Tombstoning' ([int]$tomb -le 1) "tombstoned=$tomb"
}

# ---------- NTLM-Ablehnung ----------
# Nur im GPO-Auditmodus (W19) vorhanden. Bei "Deny all accounts" (W20) lehnt SSPI vor dem
# Controller ab -- dann ist das Fehlen dieser Zeile korrekt und der Beleg kommt aus dem
# NTLM-Operational-Eventlog (Event 4004).
if (& $run 'Ntlm') {
    "--- NTLM-Ablehnungen (letzte 2 h) ---"
    Invoke-Psql @"
SELECT "Timestamp", "Action", "Details" FROM "AuditLog"
WHERE "Details" LIKE '%windows_ntlm_disabled%' AND "Timestamp" > NOW() - INTERVAL '2 hours'
ORDER BY "Timestamp" DESC LIMIT 10;
"@ -Table
    $n = Get-Scalar @"
SELECT COUNT(*) FROM "AuditLog"
WHERE "Details" LIKE '%windows_ntlm_disabled%' AND "Timestamp" > NOW() - INTERVAL '2 hours';
"@
    Add-Check 'W19. Audit windows_ntlm_disabled vorhanden (nur Auditmodus)' ([int]$n -gt 0) "COUNT=$n"

    $sessions = Get-Scalar @"
SELECT COUNT(*) FROM "AuthSessions" s
WHERE s."AuthenticationMethod" = 'Windows' AND s."CreatedAt" > NOW() - INTERVAL '5 minutes';
"@
    Add-Check 'W19. Keine frische Windows-Session durch NTLM' ($sessions -eq '0') "COUNT=$sessions"
}

# ---------- Audit-Gesamtbild ----------
if (& $run 'Audit') {
    "--- Auth-Audit der letzten 2 Stunden ---"
    Invoke-Psql @"
SELECT "Action", COUNT(*) AS "Anzahl" FROM "AuditLog"
WHERE "Timestamp" > NOW() - INTERVAL '2 hours'
  AND ("Action" LIKE 'LOGIN%' OR "Action" LIKE 'USER_WINDOWS%'
       OR "Action" LIKE 'USER_DIRECTORY%' OR "Action" LIKE 'BREAK_GLASS%')
GROUP BY "Action" ORDER BY "Action";
"@ -Table

    # Kollisionen/Identitaetskonflikte schreiben KEIN LOGIN_FAILED, nur USER_WINDOWS_REFUSED_*.
    # SIEM-Regeln, die nur auf LOGIN_* filtern, verlieren diese Faelle -- deshalb hier
    # separat sichtbar machen.
    "--- Refusals ohne LOGIN_FAILED-Gegenstueck ---"
    Invoke-Psql @"
SELECT "Timestamp", "Action", "Username" FROM "AuditLog"
WHERE "Action" LIKE 'USER_WINDOWS_REFUSED%' AND "Timestamp" > NOW() - INTERVAL '2 hours'
ORDER BY "Timestamp" DESC LIMIT 20;
"@ -Table
}

if ($results.Count -gt 0) {
    ""
    "================ DB-BELEGE ================"
    $pass = 0; $fail = 0
    foreach ($x in $results) {
        $mark = if ($x.Ok) { 'PASS' } else { 'FAIL' }
        if ($x.Ok) { $pass++ } else { $fail++ }
        "{0}  {1}  -- {2}" -f $mark, $x.Check, $x.Detail
    }
    ""
    "PASS=$pass FAIL=$fail"
    if ($fail -gt 0) { exit 1 }
}
