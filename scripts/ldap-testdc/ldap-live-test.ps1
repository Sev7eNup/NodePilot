# NodePilot LDAP Live-Testsuite gegen den Samba-Test-DC (siehe README.md in diesem Ordner).
#
# Voraussetzungen:
#   1. Test-DC-Container laeuft (docker run ... nodepilot-testdc, Port 636).
#   2. API laeuft in PHASE B (LDAP aktiv, Endpoint 127.0.0.1:636) gegen die Wegwerf-DB.
#   3. Break-Glass-Admin wurde in PHASE A gebootstrapped (Passwoerter siehe Params).
#
# Aufruf:   powershell -NoProfile -ExecutionPolicy Bypass -File .\ldap-live-test.ps1
# Optional: -IncludeOutageDrill  (stoppt/startet den Container fuer den Fail-Closed-Test)
#
# Validierter Stand 2026-07-24: 13/13 PASS + Outage-Drill PASS.
param(
    [string]$Base = 'http://localhost:5000',
    [string]$ContainerName = 'nodepilot-testdc',
    [string]$BreakGlassUser = 'breakglass.admin',
    [string]$BreakGlassPassword = 'Boot#20260724!Adm1n',
    [string]$AlicePassword = 'Login#20260724!Mv3p',
    [string]$CarolPassword = 'Login#20260724!Tw8r',
    [string]$BobPassword = 'Login#20260724!Zh5c',
    [string]$ServiceBindDn = 'CN=svc-nodepilot,CN=Users,DC=nodepilot,DC=test',
    [string]$ServiceBindPassword = 'Bind#20260724!Kq7z',
    [switch]$IncludeOutageDrill
)
$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.ArrayList

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    [void]$results.Add([pscustomobject]@{ Test = $name; Ok = $ok; Detail = $detail })
}

# POST JSON; liefert @{Status=..; Body=..}; wirft nicht bei HTTP-Fehlerstatus.
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
        return @{ Status = [int]$r.StatusCode; Body = $parsed; Raw = $r.Content }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $status = [int]$resp.StatusCode
        $raw = ''
        try { $sr = New-Object System.IO.StreamReader($resp.GetResponseStream()); $raw = $sr.ReadToEnd(); $sr.Close() } catch {}
        $parsed = $null; try { $parsed = $raw | ConvertFrom-Json } catch {}
        return @{ Status = $status; Body = $parsed; Raw = $raw }
    }
}

# SID der Allowed-Group live aus dem Container ziehen -- die Domain-SID ist pro
# Provisionierung neu, hartkodierte SIDs waeren nach jedem frischen Container falsch.
$accessSid = $null
foreach ($line in (docker exec $ContainerName ldbsearch -H /var/lib/samba/private/sam.ldb '(sAMAccountName=NodePilot-Access)' objectSid)) {
    if ($line -match '^objectSid:\s*(\S+)') { $accessSid = $Matches[1]; break }
}
if (-not $accessSid) { throw "Konnte NodePilot-Access-SID nicht aus Container '$ContainerName' lesen -- laeuft er?" }
"Allowed-Group-SID (NodePilot-Access): $accessSid"

# ---------- 1. Discovery ----------
$m = Invoke-RestMethod -Uri "$Base/api/auth/methods"
Add-Result '1. /auth/methods' ($m.ldap -eq $true -and $m.local -eq $true) ("ldap=$($m.ldap) local=$($m.local) windows=$($m.windows)")

# ---------- 2. Directory-Health (Service-Bind + BaseDn-Read uebers echte LDAPS) ----------
try { $h = Invoke-RestMethod -Uri "$Base/healthz/directory" } catch { $h = 'ERROR' }
Add-Result '2. /healthz/directory' ($h -match 'Healthy') "$h"

# ---------- 3. Lokaler Break-Glass-Login trotz aktivem LDAP (Local-Shadow) ----------
$adminSession = $null
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = $BreakGlassUser; password = $BreakGlassPassword } $null ([ref]$adminSession)
Add-Result '3. Break-Glass-Login lokal (LDAP aktiv, wird uebersprungen)' ($r.Status -eq 200 -and $r.Body.role -eq 'Admin') ("status=$($r.Status) role=$($r.Body.role)")

# ---------- 4. LDAP-Login happy path: alice (NUR in NodePilot-Admins, nested in -Access) ----------
$aliceSession = $null
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'alice.demo@nodepilot.test'; password = $AlicePassword } $null ([ref]$aliceSession)
$aliceId = $r.Body.userId
Add-Result '4. LDAP-Login alice (UPN) -> Admin via nested Group' ($r.Status -eq 200 -and $r.Body.role -eq 'Admin') ("status=$($r.Status) role=$($r.Body.role) user=$($r.Body.username)")

# ---------- 5. /auth/me mit Alice-Cookie-Session ----------
try {
    $me = Invoke-RestMethod -Uri "$Base/api/auth/me" -WebSession $aliceSession
    Add-Result '5. /auth/me (Cookie-Session)' ($me.username -eq 'alice.demo@nodepilot.test') ("username=$($me.username) role=$($me.role)")
} catch { Add-Result '5. /auth/me (Cookie-Session)' $false "EXCEPTION: $($_.Exception.Message)" }

# ---------- 6. Bare Username -> UpnSuffix-Normalisierung, gleicher User ----------
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'alice.demo'; password = $AlicePassword } $null $null
Add-Result '6. LDAP-Login alice (bare + UpnSuffix)' ($r.Status -eq 200 -and $r.Body.userId -eq $aliceId) ("status=$($r.Status) sameUserId=$($r.Body.userId -eq $aliceId)")

# ---------- 7. Falsches Passwort -> 401 (echter AD-Fehler 49) ----------
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'alice.demo@nodepilot.test'; password = 'Falsch#Passw0rd!' } $null $null
Add-Result '7. Falsches Passwort -> 401' ($r.Status -eq 401) ("status=$($r.Status)")

# ---------- 8. Leeres Passwort -> abgelehnt (H-17, unauthenticated-bind-Bypass) ----------
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'alice.demo@nodepilot.test'; password = '' } $null $null
Add-Result '8. Leeres Passwort abgelehnt (H-17)' ($r.Status -eq 400 -or $r.Status -eq 401) ("status=$($r.Status)")

# ---------- 9. carol: nur NodePilot-Access -> Viewer (kein RoleMapping-Treffer) ----------
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'carol.demo@nodepilot.test'; password = $CarolPassword } $null $null
Add-Result '9. LDAP-Login carol -> Viewer (kein RoleMapping)' ($r.Status -eq 200 -and $r.Body.role -eq 'Viewer') ("status=$($r.Status) role=$($r.Body.role)")

# ---------- 10. bob: in keiner erlaubten Gruppe -> 401 generisch, kein JIT-Row ----------
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'bob.demo@nodepilot.test'; password = $BobPassword } $null $null
Add-Result '10. bob ohne AllowedGroup -> 401' ($r.Status -eq 401) ("status=$($r.Status)")

# ---------- 11. Unbekannter Directory-User -> 401 ----------
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'unknown.user@nodepilot.test'; password = 'Irgend#Was2026!x' } $null $null
Add-Result '11. Unbekannter User -> 401' ($r.Status -eq 401) ("status=$($r.Status)")

# ---------- 12. Zweiter Alice-Login = JIT-Update, kein Duplikat ----------
$r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'alice.demo@nodepilot.test'; password = $AlicePassword } $null $null
Add-Result '12. Re-Login alice -> selber User (JIT-Update)' ($r.Status -eq 200 -and $r.Body.userId -eq $aliceId) ("status=$($r.Status) sameUserId=$($r.Body.userId -eq $aliceId)")

# ---------- 13. Admin-Settings LDAP-Probe ----------
# Gotchas (beide 2026-07-24 live erlebt):
#   a) Cookie-Werte sind URL-encodiert -- np_csrf VOR dem Header-Echo decodieren.
#   b) Der Endpoint erwartet den Wrapper { settings: { ... } }.
try {
    $csrfRaw = ($adminSession.Cookies.GetCookies([Uri]$Base) | Where-Object Name -eq 'np_csrf').Value
    $csrf = [Uri]::UnescapeDataString($csrfRaw)
    $probeBody = @{ settings = @{
        Enabled = $true; Endpoints = @('127.0.0.1:636'); Port = 636; UseSsl = $true
        BaseDn = 'DC=nodepilot,DC=test'; UpnSuffix = 'nodepilot.test'; BindTimeoutSeconds = 5
        ServiceBindDn = $ServiceBindDn; ServicePassword = $ServiceBindPassword
        AllowedGroupSids = @($accessSid)
    } }
    $r = Invoke-JsonPost "$Base/api/admin/settings/test/ldap" $probeBody @{ 'X-CSRF-Token' = $csrf } ([ref]$adminSession)
    $okFlag = $false
    if ($r.Body -and $null -ne $r.Body.ok) { $okFlag = [bool]$r.Body.ok }
    Add-Result '13. Admin LDAP-Testprobe' ($r.Status -eq 200 -and $okFlag) ("status=$($r.Status) ok=$okFlag msg=$($r.Body.message)")
} catch { Add-Result '13. Admin LDAP-Testprobe' $false "EXCEPTION: $($_.Exception.Message)" }

# ---------- 14./15. Optional: Outage-Drill (Fail-Closed + Recovery) ----------
if ($IncludeOutageDrill) {
    docker stop $ContainerName | Out-Null
    Start-Sleep 2
    $r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'alice.demo@nodepilot.test'; password = $AlicePassword } $null $null
    Add-Result '14. DC weg -> 503 fail-closed' ($r.Status -eq 503) ("status=$($r.Status)")
    docker start $ContainerName | Out-Null
    Start-Sleep 12
    $r = Invoke-JsonPost "$Base/api/auth/login" @{ username = 'alice.demo@nodepilot.test'; password = $AlicePassword } $null $null
    Add-Result '15. DC zurueck -> Login ok' ($r.Status -eq 200) ("status=$($r.Status) role=$($r.Body.role)")
}

# ---------- Ausgabe ----------
""
"================ ERGEBNIS ================"
$pass = 0; $fail = 0
foreach ($x in $results) {
    $mark = if ($x.Ok) { 'PASS' } else { 'FAIL' }
    if ($x.Ok) { $pass++ } else { $fail++ }
    "{0}  {1}  -- {2}" -f $mark, $x.Test, $x.Detail
}
""
"PASS=$pass FAIL=$fail"
if ($fail -gt 0) { exit 1 }
