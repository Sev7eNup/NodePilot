# Database evidence for the Windows SSO field test. Read-only: the script writes nothing.
#
# RUN ON: the host with Postgres access (npapi01), or any host with a matching -DbHost.
#
# Usage:
#   .\Assert-DbState.ps1 -DbPassword '<pw>'                    # all scenarios
#   .\Assert-DbState.ps1 -DbPassword '<pw>' -Scenario Ntlm     # a single scenario
#
# Always call psql with -w: without it the console password prompt blocks the script in
# non-interactive shells.
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
        # Do not name this $args: that is an automatic PowerShell variable.
        $psqlArgs = @('-w', '-h', $DbHost, '-U', $DbUser, '-d', $DbName)
        if (-not $Table) { $psqlArgs += @('-A', '-t') }
        # Pass the SQL on stdin (-f -) instead of -c: PowerShell strips double quotes from
        # arguments to a native exe, so Postgres folds the unquoted identifier to lowercase
        # and reports that the relation does not exist. EF's PascalCase table names need the
        # quotes, and stdin bypasses argument quoting entirely.
        $sql | & $PsqlPath @psqlArgs -f -
    } finally { $env:PGPASSWORD = $null }
}
function Get-Scalar([string]$sql) { ((Invoke-Psql $sql) -join '').Trim() }

$results = New-Object System.Collections.ArrayList
function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$results.Add([pscustomobject]@{ Check = $name; Ok = $ok; Detail = $detail })
}

$run = { param($s) $Scenario -eq 'All' -or $Scenario -eq $s }

# ---------- Identity: LDAP and Windows share one subject ----------
# For both paths the authority is the canonical AD namespace and the subject is the SID.
# Two rows for the same person would mean the two paths created separate accounts.
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

    # Groups live server-side, not in the token. alice inherits admission transitively.
    "--- DirectoryMemberships alice ---"
    Invoke-Psql @"
SELECT m."Authority", m."GroupKey" FROM "DirectoryMemberships" m
JOIN "Users" u ON u."Id" = m."UserId" WHERE u."Username" ILIKE 'np.alice%';
"@ -Table
}

# ---------- Gate: bob must not produce a JIT row ----------
# Admission is fail-closed: without a match in AllowedGroupSids no account is created.
# A created but role-less user would be a silent regression.
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

# ---------- Race: parallel first logins ----------
if (& $run 'Race') {
    $daveUsers = Get-Scalar "SELECT COUNT(*) FROM ""Users"" WHERE ""Username"" ILIKE 'np.dave%';"
    Add-Check 'W18. Genau ein User fuer dave' ($daveUsers -eq '1') "COUNT=$daveUsers"
    $daveIds = Get-Scalar @"
SELECT COUNT(*) FROM "ExternalIdentities" e
JOIN "Users" u ON u."Id" = e."UserId" WHERE u."Username" ILIKE 'np.dave%';
"@
    Add-Check 'W18. Genau eine Identity fuer dave' ($daveIds -eq '1') "COUNT=$daveIds"
    # Exactly one JIT_CREATED shows the unique-index race resolved correctly: the losing
    # callers take the update path instead of a second insert.
    $created = Get-Scalar @"
SELECT COUNT(*) FROM "AuditLog"
WHERE "Action" = 'USER_WINDOWS_JIT_CREATED' AND "Username" ILIKE 'np.dave%';
"@
    Add-Check 'W18. Genau ein USER_WINDOWS_JIT_CREATED fuer dave' ($created -eq '1') "COUNT=$created"
}

# ---------- Revocation: group removal / account disable ----------
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

    # Counter-check for W17: the sync must never tombstone the whole user base.
    $tomb = Get-Scalar "SELECT COUNT(*) FROM ""Users"" WHERE ""IsTombstoned"" = true;"
    Add-Check 'W17. Kein Massen-Tombstoning' ([int]$tomb -le 1) "tombstoned=$tomb"
}

# ---------- NTLM refusal ----------
# Present only in GPO audit mode (W19). Under "Deny all accounts" (W20) SSPI refuses before
# the controller runs, so a missing row is correct there and the evidence comes from the
# NTLM operational event log (event 4004).
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

# ---------- Audit overview ----------
if (& $run 'Audit') {
    "--- Auth-Audit der letzten 2 Stunden ---"
    Invoke-Psql @"
SELECT "Action", COUNT(*) AS "Anzahl" FROM "AuditLog"
WHERE "Timestamp" > NOW() - INTERVAL '2 hours'
  AND ("Action" LIKE 'LOGIN%' OR "Action" LIKE 'USER_WINDOWS%'
       OR "Action" LIKE 'USER_DIRECTORY%' OR "Action" LIKE 'BREAK_GLASS%')
GROUP BY "Action" ORDER BY "Action";
"@ -Table

    # Collisions and identity conflicts write no LOGIN_FAILED, only USER_WINDOWS_REFUSED_*.
    # SIEM rules that filter on LOGIN_* alone miss these cases, so list them separately here.
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
