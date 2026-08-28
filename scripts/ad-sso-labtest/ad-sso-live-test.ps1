# NodePilot Windows SSO/Kerberos live test suite against a real AD (see README.md).
#
# Run on the test client, in a local interactive session.
#   Over Enter-PSSession/WinRM the process has no delegatable TGT, so
#   -UseDefaultCredentials fails with 401 and looks like a product defect.
#
# Prerequisites:
#   1. Setup-LabDirectory.ps1 has run on the DC.
#   2. Setup-ApiHost.ps1 has run on npapi01 without FAIL.
#   3. API runs in PHASE B (Windows SSO + LDAPS active) against the throwaway DB.
#   4. The break-glass admin was bootstrapped in PHASE A.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\ad-sso-live-test.ps1
#   .\ad-sso-live-test.ps1 -IncludeNtlmProbe -IncludeRaceDrill -IncludeOutageDrill
#
# Always covered: W1-W3, W5, W6a, W7-W12, W25; W13/W14/W15/W16/W18/W19/W22 via switch.
# Not covered (manual, see the README section "Manuelle Testpunkte"): W0, W4, W6b
# (browser + Fiddler), W17, W20, W21, W23, W24, W26, W27.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Wegwerf-Lab-Credentials aus Setup-LabDirectory.ps1. Die Suite reicht sie an PSCredential/HttpClient weiter; SecureString wuerde nur den Copy-Paste-Ablauf des READMEs brechen, ohne das Lab-Geheimnis zu schuetzen.')]
param(
    [string]$Base = 'https://npapi01.np.lab',
    [string]$UpnSuffix = 'np.lab',
    [string]$NetbiosDomain = 'NPLAB',
    [string]$DcFqdn = 'dc01.np.lab',
    [string]$BreakGlassUser = 'breakglass.admin',
    [string]$BreakGlassPassword = 'Boot#20260802!Adm1n',
    [string]$LabPassword = 'Lab#20260802!Kq7z',
    [string]$NtlmAliasFqdn = 'npapi01-ntlm.np.lab',
    # Must match Setup-LabDirectory.ps1. The dedicated prefix keeps the suite from touching
    # foreign accounts in a lab that already has a NodePilot LDAP binding.
    [string]$UserPrefix = 'nptest',
    [string]$AccessGroup = 'NPTest-Access',

    # Optional DB verification (evidence for "no JIT row" and "exactly one user").
    [string]$PsqlPath = 'C:\NodePilot-Postgres\pgsql\bin\psql.exe',
    [string]$DbHost = '127.0.0.1',
    [string]$DbUser = 'nodepilot',
    [string]$DbName = 'nodepilot_adssotest',
    [string]$DbPassword = $null,

    # Drills
    [switch]$IncludeNtlmProbe,
    [switch]$IncludeRaceDrill,
    [switch]$IncludeDisableDrill,
    [switch]$IncludeOutageDrill,
    [switch]$IncludeRevocationDrill,
    [switch]$IncludeRateLimitDrill,
    [string]$DcVmName = $null,
    [string]$HyperVHost = $null,
    [string]$RevocationWorkflowId = $null,
    [int]$RevocationTimeoutMinutes = 16
)
$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.ArrayList
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dbReady = [bool]$DbPassword -and (Test-Path $PsqlPath)

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Ok = $ok; Detail = $detail })
}
function Add-Skip([string]$name, [string]$reason) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Ok = $null; Detail = "SKIP: $reason" })
}

# POST JSON; returns @{Status;Body;Raw}; does not throw on an HTTP error status.
function Invoke-JsonPost([string]$url, $bodyObj, [hashtable]$headers, [ref]$sessionRef) {
    $json = if ($null -ne $bodyObj) { $bodyObj | ConvertTo-Json -Depth 6 } else { '{}' }
    $p = @{ Uri = $url; Method = 'POST'; Body = $json; ContentType = 'application/json'; UseBasicParsing = $true }
    if ($headers) { $p.Headers = $headers }
    if ($sessionRef) {
        if ($sessionRef.Value) { $p.WebSession = $sessionRef.Value } else { $p.SessionVariable = 'newSession' }
    }
    try {
        $r = Invoke-WebRequest @p
        if ($sessionRef -and -not $sessionRef.Value) { $sessionRef.Value = Get-Variable -Name newSession -ValueOnly }
        $parsed = $null; try { $parsed = $r.Content | ConvertFrom-Json } catch {}
        return @{ Status = [int]$r.StatusCode; Body = $parsed; Raw = $r.Content; Headers = $r.Headers }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $raw = ''
        try { $sr = New-Object System.IO.StreamReader($resp.GetResponseStream()); $raw = $sr.ReadToEnd(); $sr.Close() } catch {}
        $parsed = $null; try { $parsed = $raw | ConvertFrom-Json } catch {}
        return @{ Status = [int]$resp.StatusCode; Body = $parsed; Raw = $raw; Headers = $null }
    }
}

# SSO login. Without $cred the handshake uses the ambient credentials of the session
# (the logged-on user); with $cred SSPI acquires a TGT for that account.
# Always a fresh session, because -WebSession and -SessionVariable are mutually exclusive.
function Invoke-SsoLogin([System.Management.Automation.PSCredential]$cred) {
    $p = @{ Uri = "$Base/api/auth/windows"; Method = 'POST'; Body = '{}'
            ContentType = 'application/json'; UseBasicParsing = $true; SessionVariable = 'ssoSession' }
    if ($cred) { $p.Credential = $cred } else { $p.UseDefaultCredentials = $true }
    try {
        $r = Invoke-WebRequest @p
        $parsed = $null; try { $parsed = $r.Content | ConvertFrom-Json } catch {}
        return @{ Status = [int]$r.StatusCode; Body = $parsed; Raw = $r.Content
                  Session = (Get-Variable -Name ssoSession -ValueOnly); Headers = $r.Headers }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $raw = ''
        try { $sr = New-Object System.IO.StreamReader($resp.GetResponseStream()); $raw = $sr.ReadToEnd(); $sr.Close() } catch {}
        $parsed = $null; try { $parsed = $raw | ConvertFrom-Json } catch {}
        return @{ Status = [int]$resp.StatusCode; Body = $parsed; Raw = $raw; Session = $null; Headers = $null }
    }
}

function New-LabCredential([string]$sam) {
    New-Object System.Management.Automation.PSCredential(
        "$NetbiosDomain\$sam", (ConvertTo-SecureString $LabPassword -AsPlainText -Force))
}

function Get-SessionCookie($session, [string]$name) {
    if (-not $session) { return $null }
    ($session.Cookies.GetCookies([Uri]$Base) | Where-Object Name -eq $name)
}

# Decode the JWT payload (base64url, padding added by hand).
function ConvertFrom-JwtPayload([string]$jwt) {
    $parts = $jwt.Split('.')
    if ($parts.Count -lt 2) { return $null }
    $p = $parts[1].Replace('-', '+').Replace('_', '/')
    switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json
}

# psql -w is required; without it the console password prompt blocks the suite.
# SQL goes through stdin (-f -) rather than -c: when calling a native exe PowerShell strips
# the double quotes from the argument, so "Users" becomes Users and Postgres answers
# 'relation "users" does not exist'. EF PascalCase tables need those quotes, and stdin
# bypasses argument quoting entirely.
#
# A psql failure must never abort the suite: under $ErrorActionPreference='Stop' PowerShell
# 5.1 turns the stderr of a native exe into a terminating NativeCommandError. Switching to
# 'Continue' locally and returning the error as a value lets the affected assertion report
# FAIL with a readable detail while the run continues.
function Invoke-Psql([string]$sql) {
    $env:PGPASSWORD = $DbPassword
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = $sql | & $PsqlPath -w -h $DbHost -U $DbUser -d $DbName -A -t -f - 2>&1
        if ($LASTEXITCODE -ne 0) { return "PSQL_ERROR($LASTEXITCODE): $((($out | Out-String) -replace '\s+', ' ').Trim())" }
        return (($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join '').Trim()
    } catch {
        return "PSQL_ERROR: $($_.Exception.Message)"
    } finally {
        $ErrorActionPreference = $prev
        $env:PGPASSWORD = $null
    }
}

$apiHost = ([Uri]$Base).Host
"Ziel: $Base  (Kerberos-SPN erwartet: HTTP/$apiHost)"
"Angemeldet als: $env:USERDOMAIN\$env:USERNAME"
if (-not $dbReady) { "DB-Verifikation deaktiviert (kein -DbPassword oder psql nicht gefunden)." }
""

# ---------- W1. Discovery ----------
$m = Invoke-RestMethod -Uri "$Base/api/auth/methods"
Add-Result 'W1. /auth/methods meldet Windows-SSO' `
    ($m.windows -eq $true -and $m.windowsEndpoint -eq '/api/auth/windows') `
    ("windows=$($m.windows) endpoint=$($m.windowsEndpoint) ldap=$($m.ldap) local=$($m.local)")

# ---------- W2. Health trio ----------
foreach ($probe in @(
    @{ N = 'W2a. /healthz/live'; U = '/healthz/live'; Match = $null }
    @{ N = 'W2b. /healthz/ready'; U = '/healthz/ready'; Match = $null }
    @{ N = 'W2c. /healthz/directory (LDAPS)'; U = '/healthz/directory'; Match = 'Healthy' }
    @{ N = 'W2d. /healthz/leader'; U = '/healthz/leader'; Match = $null }
)) {
    try {
        $r = Invoke-WebRequest -Uri "$Base$($probe.U)" -UseBasicParsing
        $ok = ([int]$r.StatusCode -eq 200) -and (-not $probe.Match -or $r.Content -match $probe.Match)
        Add-Result $probe.N $ok "status=$([int]$r.StatusCode) body=$($r.Content.Trim())"
    } catch {
        # Health endpoints answer 503 on failure, which makes Invoke-WebRequest throw.
        # Log the status code instead of the localized exception text: "status=503" is
        # usable evidence, a translated "the remote server returned an error" is not.
        $st = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        Add-Result $probe.N $false $(if ($st -gt 0) { "status=$st" } else { "EXCEPTION: $($_.Exception.Message)" })
    }
}

# ---------- W3. Break-glass login while SSO is active ----------
# The local emergency path has to survive next to SSO; otherwise a DC outage leaves no
# way back into the application.
$adminSession = $null
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = $BreakGlassUser; password = $BreakGlassPassword } $null ([ref]$adminSession)
Add-Result 'W3. Break-Glass-Login lokal (SSO aktiv)' ($r.Status -eq 200 -and $r.Body.role -eq 'Admin') `
    ("status=$($r.Status) role=$($r.Body.role)")

# ---------- W5. Happy Path ----------
# Two logins, deliberately separate:
#   (a) Ambient credentials, the path the browser button takes. Whoever runs the suite is
#       arbitrary, so only the status code is checked, not the role. This login also pulls
#       the service ticket that W6a proves via klist.
#   (b) Explicit <prefix>.alice, the anchor for the identity chain W7/W9/W10, so the chain
#       never assumes that the logged-on user is alice.
$sso = Invoke-SsoLogin $null
Add-Result 'W5a. SSO mit Ambient-Credentials (Browser-Pfad)' ($sso.Status -eq 200) `
    ("status=$($sso.Status) role=$($sso.Body.role) user=$($sso.Body.username)")

# With Windows SSO the JWT must never appear in the body (ambient-credential driven flow):
# AuthController calls SessionResult(..., includeToken: false).
#
# Only checkable when the login succeeded at all: a 401 body carries no token anyway, so a
# PASS from it would be green without measuring anything.
if ($sso.Status -ne 200) {
    Add-Skip 'W5b. Kein Token im Response-Body' "W5a lieferte $($sso.Status) -- kein Erfolgs-Body zu pruefen"
} else {
    $hasToken = [bool]($sso.Body -and $sso.Body.PSObject.Properties.Name -contains 'token' -and $sso.Body.token)
    Add-Result 'W5b. Kein Token im Response-Body' (-not $hasToken) "tokenImBody=$hasToken"
}

$authCookie = Get-SessionCookie $sso.Session 'np_auth'
$csrfCookie = Get-SessionCookie $sso.Session 'np_csrf'
Add-Result 'W5c. np_auth (httpOnly) + np_csrf gesetzt' `
    (($null -ne $authCookie) -and $authCookie.HttpOnly -and ($null -ne $csrfCookie)) `
    ("np_auth=$($null -ne $authCookie) httpOnly=$($authCookie.HttpOnly) np_csrf=$($null -ne $csrfCookie)")

# ---------- W6a. Kerberos evidence on the HTTP stack ----------
# klist shows the service ticket the handshake pulled. An NTLM fallback leaves no HTTP/
# entry here. The browser/Fiddler evidence W6b stays manual.
try {
    $klist = (& klist tickets 2>&1 | Out-String)
    $hasTicket = $klist -match [regex]::Escape("HTTP/$apiHost")
    Add-Result 'W6a. klist zeigt Service-Ticket HTTP/<fqdn>' $hasTicket `
        $(if ($hasTicket) { "Ticket HTTP/$apiHost vorhanden -- Kerberos, kein NTLM." } else { "Kein HTTP/$apiHost in klist -- Handshake lief moeglicherweise ueber NTLM." })
} catch {
    Add-Result 'W6a. klist zeigt Service-Ticket HTTP/<fqdn>' $false "EXCEPTION: $($_.Exception.Message)"
}

# ---------- W5d. Explicit alice: transitive group resolution ----------
# alice is only in the Admins group, which is itself a member of the access group from
# AllowedGroupSids. A 200 with Role=Admin therefore proves both admission through the
# nested membership (tokenGroups, not memberOf) and the role mapping.
$ssoAlice = Invoke-SsoLogin (New-LabCredential "$UserPrefix.alice")
$aliceId = $ssoAlice.Body.userId
Add-Result 'W5d. SSO alice -> 200 + Admin (transitiv ueber verschachtelte Gruppe)' `
    ($ssoAlice.Status -eq 200 -and $ssoAlice.Body.role -eq 'Admin') `
    ("status=$($ssoAlice.Status) role=$($ssoAlice.Body.role) user=$($ssoAlice.Body.username)")

# ---------- W7. Session valid ----------
try {
    $me = Invoke-RestMethod -Uri "$Base/api/auth/me" -WebSession $ssoAlice.Session
    # Compare against the login the session came from, not against an expected name.
    # Do not name this $matches; that is the automatic variable of -match.
    $identityOk = ($me.username -eq $ssoAlice.Body.username) -or
                  ($aliceId -and $me.userId -eq $aliceId) -or
                  ($aliceId -and $me.id -eq $aliceId)
    Add-Result 'W7. /auth/me mit SSO-Cookie' $identityOk `
        ("username=$($me.username) role=$($me.role) erwartet=$($ssoAlice.Body.username)")
} catch {
    # Status code instead of the localized exception; see the comment at W2.
    $st = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
    Add-Result 'W7. /auth/me mit SSO-Cookie' $false $(if ($st -gt 0) { "status=$st" } else { "EXCEPTION: $($_.Exception.Message)" })
}

# ---------- W8. No groups in the JWT ----------
# Directory groups stay server-side in DirectoryMemberships. A group claim in the token
# would be a silent authorization regression: the token would outlive a revocation until
# it expires, while the server-side check takes effect immediately.
#
# The check looks for the absence of forbidden claims instead of a positive whitelist: the
# token carries the long claim URIs (.../claims/nameidentifier, /name, /role), not the
# short forms nameid/unique_name/role, so a whitelist would break on any change to
# OutboundClaimTypeMap. The pattern catches both spellings, because a group claim in URI
# form is called ".../claims/groupsid".
if ($authCookie) {
    $payload = ConvertFrom-JwtPayload ([Uri]::UnescapeDataString($authCookie.Value))
    $claimNames = @($payload.PSObject.Properties.Name)
    $forbidden = @($claimNames | Where-Object { $_ -match '(?i)group|primarysid|objectsid|(^|/)sid$' })
    Add-Result 'W8. JWT enthaelt keine Gruppen-/SID-Claims' ($forbidden.Count -eq 0) `
        ("claims=[$($claimNames -join ',')] verboten=[$($forbidden -join ',')]")
} else {
    Add-Result 'W8. JWT enthaelt keine Gruppen-/SID-Claims' $false 'np_auth-Cookie nicht verfuegbar'
}

# ---------- W9. LDAP and Windows resolve the same identity ----------
# Both paths share the canonical AD subject (objectSid) under the same authority.
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = "$UserPrefix.alice@$UpnSuffix"; password = $LabPassword } $null $null
$sameUser = ($r.Status -eq 200 -and $r.Body.userId -eq $aliceId)
Add-Result 'W9. LDAP-Login = gleiche UserId wie SSO' $sameUser `
    ("status=$($r.Status) ldapUserId=$($r.Body.userId) ssoUserId=$aliceId")

# Without a successful SSO login $aliceId is empty, and an empty UUID literal is a Postgres
# syntax error rather than a result. Make the dependency explicit.
if (-not $dbReady) {
    Add-Skip 'W9b. Genau eine ExternalIdentity fuer alice' 'keine DB-Verbindung konfiguriert'
} elseif ([string]::IsNullOrWhiteSpace($aliceId)) {
    Add-Skip 'W9b. Genau eine ExternalIdentity fuer alice' 'W5 lieferte keine userId -- nichts zu pruefen'
} else {
    $cnt = Invoke-Psql "SELECT COUNT(*) FROM ""ExternalIdentities"" e JOIN ""Users"" u ON u.""Id"" = e.""UserId"" WHERE u.""Id"" = '$aliceId' AND e.""Authority"" = 'urn:nodepilot:identity:active-directory';"
    Add-Result 'W9b. Genau eine ExternalIdentity fuer alice' ($cnt -eq '1') "COUNT=$cnt"
}

# ---------- W10. Re-login = JIT update, no duplicate ----------
# As alice again; the comparison has to cover the same person as W5d.
$sso2 = Invoke-SsoLogin (New-LabCredential "$UserPrefix.alice")
# The equality check only means something if both runs returned a UserId; otherwise
# "$null -eq $null" reports a misleading sameUserId=True next to a 401.
$sameUser = (-not [string]::IsNullOrWhiteSpace($aliceId)) -and ($sso2.Body.userId -eq $aliceId)
Add-Result 'W10. Zweiter SSO-Login alice -> gleiche UserId' ($sso2.Status -eq 200 -and $sameUser) `
    ("status=$($sso2.Status) sameUserId=$sameUser (W5d=$aliceId W10=$($sso2.Body.userId))")

# ---------- W11. carol: allowed group without RoleMapping -> Viewer ----------
$r = Invoke-SsoLogin (New-LabCredential "$UserPrefix.carol")
Add-Result 'W11. SSO carol -> Viewer (kein RoleMapping)' ($r.Status -eq 200 -and $r.Body.role -eq 'Viewer') `
    ("status=$($r.Status) role=$($r.Body.role)")

# ---------- W12. bob: in no AllowedGroup -> 401, no JIT row ----------
$r = Invoke-SsoLogin (New-LabCredential "$UserPrefix.bob")
Add-Result 'W12a. SSO bob ohne AllowedGroup -> 401' ($r.Status -eq 401) "status=$($r.Status)"
if ($dbReady) {
    $cnt = Invoke-Psql "SELECT COUNT(*) FROM ""Users"" WHERE ""Username"" ILIKE '$UserPrefix.bob%';"
    Add-Result 'W12b. Kein JIT-Row fuer bob' ($cnt.Trim() -eq '0') "COUNT=$($cnt.Trim())"
} else {
    Add-Skip 'W12b. Kein JIT-Row fuer bob' 'keine DB-Verbindung konfiguriert'
}

# ---------- W25. CSRF bootstrap exemption ----------
# CsrfMiddleware exempts /api/auth/windows as a cookie bootstrap path: a stale np_auth
# without a matching np_csrf must not produce a 403, or a user with an expired session
# could never get back in through SSO.
if ($authCookie) {
    $stale = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $stale.Cookies.Add((New-Object System.Net.Cookie('np_auth', $authCookie.Value, '/', $apiHost)))
    try {
        $r = Invoke-WebRequest -Uri "$Base/api/auth/windows" -Method POST -Body '{}' `
            -ContentType 'application/json' -UseBasicParsing -WebSession $stale -UseDefaultCredentials
        Add-Result 'W25. SSO ohne np_csrf -> kein 403' ([int]$r.StatusCode -eq 200) "status=$([int]$r.StatusCode)"
    } catch {
        $st = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        Add-Result 'W25. SSO ohne np_csrf -> kein 403' $false "status=$st"
    }
} else {
    Add-Skip 'W25. SSO ohne np_csrf -> kein 403' 'np_auth-Cookie nicht verfuegbar'
}

# ---------- W18. Race on first login (optional) ----------
# <prefix>.dave must never have logged in before, otherwise the drill only exercises the
# update path. Start-Job instead of runspaces: each job carries the credentials explicitly,
# so ticket sharing cannot distort the result.
if ($IncludeRaceDrill) {
    $jobs = 1..5 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($url, $user, $pass)
            $c = New-Object System.Management.Automation.PSCredential(
                $user, (ConvertTo-SecureString $pass -AsPlainText -Force))
            try {
                [int](Invoke-WebRequest -Uri $url -Method POST -Body '{}' -ContentType 'application/json' `
                    -Credential $c -UseBasicParsing).StatusCode
            } catch {
                if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
            }
        } -ArgumentList "$Base/api/auth/windows", "$NetbiosDomain\$UserPrefix.dave", $LabPassword
    }
    $codes = @($jobs | Wait-Job -Timeout 120 | Receive-Job)
    $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
    Add-Result 'W18a. 5 parallele Erst-Logins -> alle 200' `
        (($codes.Count -eq 5) -and (@($codes | Where-Object { $_ -ne 200 }).Count -eq 0)) `
        ("codes=$($codes -join ',')")
    if ($dbReady) {
        $u = Invoke-Psql "SELECT COUNT(*) FROM ""Users"" WHERE ""Username"" ILIKE '$UserPrefix.dave%';"
        $e = Invoke-Psql "SELECT COUNT(*) FROM ""ExternalIdentities"" e JOIN ""Users"" u ON u.""Id"" = e.""UserId"" WHERE u.""Username"" ILIKE '$UserPrefix.dave%';"
        Add-Result 'W18b. Genau ein User + eine Identity fuer dave' `
            (($u.Trim() -eq '1') -and ($e.Trim() -eq '1')) "users=$($u.Trim()) identities=$($e.Trim())"
    } else {
        Add-Skip 'W18b. Genau ein User + eine Identity fuer dave' 'keine DB-Verbindung konfiguriert'
    }
} else {
    Add-Skip 'W18. Race beim Erst-Login' 'nicht angefordert (-IncludeRaceDrill)'
}

# ---------- W19. NTLM negative test (optional) ----------
if ($IncludeNtlmProbe) {
    $probe = Join-Path $scriptDir 'Invoke-NtlmProbe.ps1'
    $p = & $probe -Base $Base -NtlmAliasFqdn $NtlmAliasFqdn -NetbiosDomain $NetbiosDomain `
        -SamAccountName "$UserPrefix.alice" -Password $LabPassword -PassThruResult
    # A 401 alone is inconclusive. The application rejection proves that NTLM reached the server.
    Add-Result 'W19a. Erzwungenes NTLM -> von der ANWENDUNG abgelehnt' `
        ($p.Status -eq 401 -and $p.AppRejected) `
        $(if ($p.AppRejected) { "status=$($p.Status), Ablehnungsmeldung vorhanden" }
          else { "status=$($p.Status) UNSCHLUESSIG -- kein NTLM-Versuch beim Server angekommen; -Mode Alias noetig. body=$($p.Body)" })
    Add-Result 'W19b. Kein np_auth-Cookie bei NTLM' (-not $p.SetAuthCookie) "setAuthCookie=$($p.SetAuthCookie)"
} else {
    Add-Skip 'W19. NTLM-Negativtest' 'nicht angefordert (-IncludeNtlmProbe)'
}

# ---------- W13. Disabled AD account (optional) ----------
# Requires RSAT AD PowerShell on the client. Uses <prefix>.erin so alice remains an admin.
if ($IncludeDisableDrill) {
    try {
        Import-Module ActiveDirectory -ErrorAction Stop
        $r = Invoke-SsoLogin (New-LabCredential "$UserPrefix.erin")
        Add-Result 'W13a. erin aktiv -> SSO ok' ($r.Status -eq 200) "status=$($r.Status) role=$($r.Body.role)"
        Disable-ADAccount -Identity "$UserPrefix.erin" -Server $DcFqdn
        Start-Sleep -Seconds 5
        $r = Invoke-SsoLogin (New-LabCredential "$UserPrefix.erin")
        # AD may reject the bind before the API returns 401. Either result passes if no session
        # exists.
        Add-Result 'W13b. erin deaktiviert -> kein Login' ($r.Status -ne 200) "status=$($r.Status)"
        Enable-ADAccount -Identity "$UserPrefix.erin" -Server $DcFqdn
        "$UserPrefix.erin wieder aktiviert."
    } catch {
        Add-Result 'W13. Deaktivierter AD-Account' $false "EXCEPTION: $($_.Exception.Message)"
    }
} else {
    Add-Skip 'W13. Deaktivierter AD-Account' 'nicht angefordert (-IncludeDisableDrill)'
}

# ---------- W15/W16. Revocation after group removal (optional) ----------
# Directory synchronization reacts to removal; authorization staleness enforces the upper bound.
if ($IncludeRevocationDrill) {
    try {
        Import-Module ActiveDirectory -ErrorAction Stop
        $erin = Invoke-SsoLogin (New-LabCredential "$UserPrefix.erin")
        if ($erin.Status -ne 200) { throw "Vorbedingung verletzt: erin-Login lieferte $($erin.Status)" }

        # Use a local variable because [ref] does not reliably update a hashtable entry.
        $erinSession = $erin.Session
        $execId = $null
        if ($RevocationWorkflowId) {
            # Decode the URL-encoded np_csrf value before echoing it into the header.
            $erinCsrf = [Uri]::UnescapeDataString((Get-SessionCookie $erinSession 'np_csrf').Value)
            $x = Invoke-JsonPost "$Base/api/workflows/$RevocationWorkflowId/execute" `
                @{ parameters = @{}; timeoutSeconds = 900 } `
                @{ 'X-CSRF-Token' = $erinCsrf } ([ref]$erinSession)
            $execId = $x.Body.executionId
            "Execution gestartet: $execId (status=$($x.Status))"
        }

        Remove-ADGroupMember -Identity $AccessGroup -Members "$UserPrefix.erin" -Server $DcFqdn -Confirm:$false
        $t0 = Get-Date
        $revoked = $false
        while (((Get-Date) - $t0).TotalMinutes -lt $RevocationTimeoutMinutes) {
            Start-Sleep -Seconds 15
            try { Invoke-RestMethod -Uri "$Base/api/auth/me" -WebSession $erinSession | Out-Null }
            catch { $revoked = $true; break }
        }
        $elapsed = ((Get-Date) - $t0).TotalMinutes
        Add-Result 'W15. Gruppenentzug -> Session revoked <= 15 min' `
            ($revoked -and $elapsed -le 15) ("revoked=$revoked nach {0:N1} min" -f $elapsed)

        if ($execId) {
            $st = try { (Invoke-RestMethod -Uri "$Base/api/executions/$execId" -WebSession $adminSession).status } catch { 'UNBEKANNT' }
            Add-Result 'W16. Laufende Execution gestoppt' ($st -in @('Cancelled', 'Failed')) "executionStatus=$st"
        } else {
            Add-Skip 'W16. Laufende Execution gestoppt' 'kein -RevocationWorkflowId uebergeben'
        }

        if ($dbReady) {
            $status = Invoke-Psql "SELECT ""DirectorySyncStatus"" FROM ""Users"" WHERE ""Username"" ILIKE '$UserPrefix.erin%';"
            Add-Result 'W15b. DirectorySyncStatus = AccessRevoked' ($status.Trim() -eq 'AccessRevoked') "status=$($status.Trim())"
        } else {
            Add-Skip 'W15b. DirectorySyncStatus = AccessRevoked' 'keine DB-Verbindung konfiguriert'
        }

        Add-ADGroupMember -Identity $AccessGroup -Members "$UserPrefix.erin" -Server $DcFqdn
        "$UserPrefix.erin wieder in $AccessGroup aufgenommen."
    } catch {
        Add-Result 'W15/W16. Revocation-Drill' $false "EXCEPTION: $($_.Exception.Message)"
    }
} else {
    Add-Skip 'W15/W16. Revocation-Drill' 'nicht angefordert (-IncludeRevocationDrill)'
}

# ---------- W14. DC outage fail-closed test (optional) ----------
# Require a healthy directory first because LDAPS misconfiguration returns the same reason code.
if ($IncludeOutageDrill) {
    $vmParams = @{ Name = $DcVmName }
    if ($HyperVHost) { $vmParams['ComputerName'] = $HyperVHost }
    $paused = $false
    try {
        if ($DcVmName) { Suspend-VM @vmParams -ErrorAction Stop; $paused = $true; "DC-VM '$DcVmName' pausiert." }
        else { Read-Host "DC jetzt manuell anhalten (VM pausieren oder Port 636 blocken), dann ENTER" | Out-Null }
        Start-Sleep -Seconds 5

        $r = Invoke-SsoLogin $null
        Add-Result 'W14a. DC weg -> 503 fail-closed' ($r.Status -eq 503) "status=$($r.Status)"

        $bg = Invoke-JsonPost "$Base/api/auth/login" @{ username = $BreakGlassUser; password = $BreakGlassPassword } $null $null
        Add-Result 'W14b. Break-Glass bleibt erreichbar' ($bg.Status -eq 200) "status=$($bg.Status)"

        try {
            $ready = Invoke-WebRequest -Uri "$Base/healthz/ready" -UseBasicParsing
            Add-Result 'W14c. /healthz/ready bleibt 200 (nur DB)' ([int]$ready.StatusCode -eq 200) "status=$([int]$ready.StatusCode)"
        } catch {
            Add-Result 'W14c. /healthz/ready bleibt 200 (nur DB)' $false "EXCEPTION: $($_.Exception.Message)"
        }
    } finally {
        if ($paused) { Resume-VM @vmParams -ErrorAction SilentlyContinue; "DC-VM fortgesetzt." }
        else { Read-Host "DC wieder starten, dann ENTER" | Out-Null }
        Start-Sleep -Seconds 20
    }
    $r = Invoke-SsoLogin $null
    Add-Result 'W14d. DC zurueck -> SSO wieder ok' ($r.Status -eq 200) "status=$($r.Status)"
} else {
    Add-Skip 'W14. DC-Ausfall-Drill' 'nicht angefordert (-IncludeOutageDrill)'
}

# ---------- W22. Rate limit (optional, runs last) ----------
# The anonymous Negotiate challenge also consumes a token because rate limiting precedes auth.
if ($IncludeRateLimitDrill) {
    $codes = @()
    for ($i = 0; $i -lt 60; $i++) {
        $r = Invoke-SsoLogin $null
        $codes += $r.Status
        if ($r.Status -eq 429) { break }
    }
    $hit = @($codes | Where-Object { $_ -eq 429 }).Count -gt 0
    $successes = @($codes | Where-Object { $_ -eq 200 }).Count
    Add-Result 'W22. Rate-Limit greift (429)' $hit `
        ("429 nach $successes erfolgreichen Logins; Tokens pro Login ~ {0:N1}" -f $(if ($successes -gt 0) { 50 / $successes } else { 0 }))
    "Hinweis: die Login-IP ist jetzt fuer bis zu 60 s gedrosselt."
} else {
    Add-Skip 'W22. Rate-Limit' 'nicht angefordert (-IncludeRateLimitDrill)'
}

# ---------- Ausgabe ----------
""
"================ ERGEBNIS ================"
$pass = 0; $fail = 0; $skip = 0
foreach ($x in $results) {
    if ($null -eq $x.Ok) { $mark = 'SKIP'; $skip++ }
    elseif ($x.Ok) { $mark = 'PASS'; $pass++ }
    else { $mark = 'FAIL'; $fail++ }
    "{0}  {1}  -- {2}" -f $mark, $x.Test, $x.Detail
}
""
"PASS=$pass FAIL=$fail SKIP=$skip"
""
"Manuell nachzuweisen (siehe README, 'Manuelle Testpunkte'):"
"  W0/W4 Boot-Validator, W6b Browser+Fiddler, W17 Falsch-Tombstoning, W20/W21"
"  Restrict-NTLM-GPO, W23 Windows-only, W24 Restart-Semantik, W26 Kollision, W27 SPN-Duplikat."
if ($fail -gt 0) { exit 1 }
