# Preflight for the NodePilot API member server (npapi01) before the Windows SSO field test.
#
# RUN ON: npapi01, elevated (setspn, certificate store and firewall need privileges).
# Read-only: the script changes nothing, it only checks and reports PASS/WARN/FAIL.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-ApiHost.ps1 `
#            -ApiFqdn npapi01.np.lab -NtlmAliasFqdn npapi01-ntlm.np.lab -DcFqdn dc01.np.lab
#
# Every FAIL here later produces a Kerberos error that looks like a product defect, so the
# preflight has to be green before the test matrix runs.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Wegwerf-Lab-Credentials. Der LDAPS-Bind-Check spiegelt bewusst exakt das, was der API-Prozess tut -- NetworkCredential nimmt Klartext entgegen.')]
param(
    [string]$ApiFqdn = 'npapi01.np.lab',
    [string]$NtlmAliasFqdn = 'npapi01-ntlm.np.lab',
    [string]$DcFqdn = 'dc01.np.lab',
    [string]$BaseDn = 'DC=np,DC=lab',
    [string]$ServiceBindDn = 'CN=svc-npdir,OU=NodePilot,DC=np,DC=lab',
    [string]$ServiceBindPassword = 'Lab#20260802!Kq7z',
    [string]$CertificateThumbprint = $null,
    [int]$MaxClockSkewSeconds = 30
)
$ErrorActionPreference = 'Continue'
$results = New-Object System.Collections.ArrayList

function Add-Check([string]$name, [string]$verdict, [string]$detail) {
    [void]$results.Add([pscustomobject]@{ Check = $name; Verdict = $verdict; Detail = $detail })
}

# ---------- 1. Domain join ----------
# Without a domain join the process has no key to decrypt the service ticket. ASP.NET
# Negotiate uses SSPI on Windows; there is no keytab equivalent here.
$cs = Get-CimInstance Win32_ComputerSystem
if ($cs.PartOfDomain) {
    Add-Check '1. Domain-Join' 'PASS' "Domain=$($cs.Domain) Host=$($cs.Name)"
} else {
    Add-Check '1. Domain-Join' 'FAIL' 'Host ist NICHT domaenengejoint -- Negotiate kann nicht funktionieren.'
}

# ---------- 2. Clock skew against the DC ----------
# Kerberos tolerates +/-5 min. A larger skew returns KRB_AP_ERR_SKEW, which looks like a
# wrong SPN.
try {
    $chart = & w32tm /stripchart /computer:$DcFqdn /samples:3 /dataonly 2>&1 | Out-String
    # w32tm prints the offset with a dot as decimal separator on any locale ("-00.0010514s"),
    # and the PowerShell [double] cast converts invariantly, so it reads that correctly.
    # Do not switch to [double]::Parse(): it uses the current culture and would read
    # "-00.0010514" as -10514 under de-DE.
    $offsets = [regex]::Matches($chart, '([+-]?\d+\.\d+)s') | ForEach-Object { [double]$_.Groups[1].Value }
    if ($offsets.Count -gt 0) {
        $worst = ($offsets | ForEach-Object { [math]::Abs($_) } | Measure-Object -Maximum).Maximum
        $v = if ($worst -le $MaxClockSkewSeconds) { 'PASS' } else { 'FAIL' }
        Add-Check '2. Zeitversatz zum DC' $v ("max |offset| = {0:N3}s (Grenze {1}s)" -f $worst, $MaxClockSkewSeconds)
    } else {
        Add-Check '2. Zeitversatz zum DC' 'WARN' "w32tm lieferte keine Messwerte: $($chart.Trim())"
    }
} catch {
    Add-Check '2. Zeitversatz zum DC' 'WARN' "w32tm fehlgeschlagen: $($_.Exception.Message)"
}

# ---------- 3. DNS: API FQDN and NTLM alias point at this host ----------
# The alias must resolve to the same IP (same Kestrel) but must not carry an SPN; that is
# the deterministic NTLM trigger for W19.
$localIps = @(Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -ne '127.0.0.1' } | Select-Object -ExpandProperty IPAddress)
foreach ($pair in @(@{ N = '3a. DNS API-FQDN'; F = $ApiFqdn }, @{ N = '3b. DNS NTLM-Alias'; F = $NtlmAliasFqdn })) {
    try {
        $ips = @(Resolve-DnsName -Name $pair.F -Type A -ErrorAction Stop |
            Where-Object QueryType -eq 'A' | Select-Object -ExpandProperty IPAddress)
        $match = @($ips | Where-Object { $localIps -contains $_ })
        $v = if ($match.Count -gt 0) { 'PASS' } else { 'FAIL' }
        Add-Check $pair.N $v "$($pair.F) -> $($ips -join ',') (lokal: $($localIps -join ','))"
    } catch {
        Add-Check $pair.N 'FAIL' "Aufloesung fehlgeschlagen: $($_.Exception.Message)"
    }
}

# Chromium canonicalizes the host and builds the SPN from the A record target, so a CNAME
# to another name produces an SPN that nobody has registered.
foreach ($f in @($ApiFqdn, $NtlmAliasFqdn)) {
    $cname = @(Resolve-DnsName -Name $f -Type CNAME -ErrorAction SilentlyContinue |
        Where-Object QueryType -eq 'CNAME')
    if ($cname.Count -gt 0) {
        Add-Check "3c. CNAME-Kanonisierung ($f)" 'WARN' `
            "$f ist ein CNAME auf $($cname[0].NameHost) -- Chromium bildet den SPN aus dem A-Record-Ziel. Im Lab direkten A-Record verwenden."
    }
}

# ---------- 4. SPN state ----------
# Variant A (LocalSystem / machine account): HOST/<fqdn> covers HTTP/ implicitly via
# sPNMappings, while an explicit HTTP/ SPN on another account wins and breaks the
# handshake. Variant B (gMSA): exactly one explicit hit is expected.
$machine = "$($cs.Name)$"
$spnList = (& setspn -L $machine 2>&1 | Out-String)
$hasHostSpn = $spnList -match [regex]::Escape("HOST/$ApiFqdn")
Add-Check '4a. HOST-SPN auf dem Maschinenkonto' $(if ($hasHostSpn) { 'PASS' } else { 'WARN' }) `
    $(if ($hasHostSpn) { "HOST/$ApiFqdn vorhanden (deckt HTTP/ implizit ab)" } else { "HOST/$ApiFqdn nicht gefunden -- bei Variante A pruefen" })

$spnQuery = (& setspn -Q "HTTP/$ApiFqdn" 2>&1 | Out-String)
$spnHits = @([regex]::Matches($spnQuery, '(?im)^\s*CN=.+$') | ForEach-Object { $_.Value.Trim() })
if ($spnHits.Count -eq 0) {
    Add-Check '4b. Expliziter HTTP-SPN' 'PASS' "Kein expliziter HTTP/$ApiFqdn -- korrekt fuer Variante A (LocalSystem)."
} elseif ($spnHits.Count -eq 1) {
    Add-Check '4b. Expliziter HTTP-SPN' 'PASS' "Genau ein Treffer: $($spnHits[0]) -- korrekt fuer Variante B (gMSA/Dienstkonto)."
} else {
    Add-Check '4b. Expliziter HTTP-SPN' 'FAIL' "MEHRERE Treffer ($($spnHits.Count)) -- Kerberos bricht auf KRB_AP_ERR_MODIFIED oder still auf NTLM."
}

# The NTLM alias has to stay SPN-free, otherwise W19 no longer falls back to NTLM.
$aliasQuery = (& setspn -Q "HTTP/$NtlmAliasFqdn" 2>&1 | Out-String)
$aliasHits = @([regex]::Matches($aliasQuery, '(?im)^\s*CN=.+$'))
Add-Check '4c. NTLM-Alias ist SPN-frei' $(if ($aliasHits.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($aliasHits.Count -eq 0) { "Kein HTTP/$NtlmAliasFqdn registriert -- NTLM-Fallback ist deterministisch." } else { "SPN vorhanden -- W19 wuerde Kerberos statt NTLM ausloesen." })

$dup = (& setspn -X 2>&1 | Out-String)
Add-Check '4d. Domaenenweite SPN-Duplikate' $(if ($dup -match 'found 0 group') { 'PASS' } else { 'WARN' }) `
    (($dup -split "`n" | Where-Object { $_ -match 'duplicate|found \d+ group' } | Select-Object -First 2) -join ' | ')

# ---------- 5. Encryption types ----------
# An RC4-only account plus an AES-enforcing domain policy gives KDC_ERR_ETYPE_NOSUPP.
try {
    $etypes = (Get-ADComputer $cs.Name -Properties 'msDS-SupportedEncryptionTypes' -Server $DcFqdn -ErrorAction Stop).'msDS-SupportedEncryptionTypes'
    # Bit 3 (0x08) = AES128, Bit 4 (0x10) = AES256.
    $hasAes = ($null -ne $etypes) -and (($etypes -band 0x18) -ne 0)
    Add-Check '5. Kerberos-Verschluesselungstypen' $(if ($hasAes -or $null -eq $etypes) { 'PASS' } else { 'WARN' }) `
        "msDS-SupportedEncryptionTypes=$etypes (AES=$hasAes; leer = Domain-Default)"
} catch {
    Add-Check '5. Kerberos-Verschluesselungstypen' 'WARN' "Nicht lesbar (RSAT fehlt?): $($_.Exception.Message)"
}

# ---------- 6. Kestrel certificate: the SAN must carry both names ----------
# One chain: URL host = SPN host = certificate SAN. The NTLM alias needs the second SAN so
# that W19 does not produce a certificate warning interstitial.
try {
    $certs = @(Get-ChildItem Cert:\LocalMachine\My)
    if ($CertificateThumbprint) {
        $certs = @($certs | Where-Object Thumbprint -eq ($CertificateThumbprint -replace '\s', ''))
    }
    $hit = $null
    foreach ($c in $certs) {
        $sanExt = $c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
        $san = if ($sanExt) { $sanExt.Format($false) } else { '' }
        if ($san -match [regex]::Escape($ApiFqdn)) { $hit = [pscustomobject]@{ Cert = $c; San = $san }; break }
    }
    if ($null -eq $hit) {
        Add-Check '6a. Kestrel-Zertifikat' 'FAIL' "Kein Zertifikat in LocalMachine\My mit SAN $ApiFqdn gefunden."
    } else {
        Add-Check '6a. Kestrel-Zertifikat' 'PASS' "Thumbprint=$($hit.Cert.Thumbprint) NotAfter=$($hit.Cert.NotAfter.ToString('yyyy-MM-dd'))"
        $aliasOk = $hit.San -match [regex]::Escape($NtlmAliasFqdn)
        Add-Check '6b. SAN enthaelt den NTLM-Alias' $(if ($aliasOk) { 'PASS' } else { 'FAIL' }) "SAN: $($hit.San)"
        $hasKey = $hit.Cert.HasPrivateKey
        Add-Check '6c. Privater Schluessel vorhanden' $(if ($hasKey) { 'PASS' } else { 'FAIL' }) "HasPrivateKey=$hasKey"
    }
} catch {
    Add-Check '6a. Kestrel-Zertifikat' 'FAIL' "EXCEPTION: $($_.Exception.Message)"
}

# ---------- 7. LDAPS with strict certificate validation ----------
# Mirrors exactly what SystemLdapConnectionAdapter does: port 636, SSL, no validation
# callback. The DC chain must be in LocalMachine\Root of the API host; there is no
# in-app bypass.
try {
    Add-Type -AssemblyName System.DirectoryServices.Protocols
    $id = New-Object System.DirectoryServices.Protocols.LdapDirectoryIdentifier($DcFqdn, 636)
    $cred = New-Object System.Net.NetworkCredential($ServiceBindDn, $ServiceBindPassword)
    $conn = New-Object System.DirectoryServices.Protocols.LdapConnection($id, $cred)
    $conn.SessionOptions.SecureSocketLayer = $true
    $conn.SessionOptions.ProtocolVersion = 3
    $conn.SessionOptions.ReferralChasing = [System.DirectoryServices.Protocols.ReferralChasingOptions]::None
    $conn.AuthType = [System.DirectoryServices.Protocols.AuthType]::Basic
    $conn.Timeout = [TimeSpan]::FromSeconds(10)
    $conn.Bind()
    $req = New-Object System.DirectoryServices.Protocols.SearchRequest(
        $BaseDn, '(objectClass=domain)', [System.DirectoryServices.Protocols.SearchScope]::Base, 'distinguishedName')
    $resp = $conn.SendRequest($req)
    Add-Check '7. LDAPS-Bind + BaseDn-Read' 'PASS' "636/SSL ok, $($resp.Entries.Count) Eintrag(e) unter $BaseDn"
    $conn.Dispose()
} catch {
    # Error 81 ("server unavailable") also masks certificate problems, hence the explicit
    # hint instead of a bare exception.
    Add-Check '7. LDAPS-Bind + BaseDn-Read' 'FAIL' `
        "$($_.Exception.Message) | Bei LdapException 81: DC-Zertifikat/SAN, CA-Trust im LocalMachine\Root, FQDN statt IP pruefen."
}

# ---------- 8. Firewall and port reachability ----------
foreach ($p in @(@{ N = '8a. LDAPS 636 zum DC'; H = $DcFqdn; P = 636 })) {
    $t = Test-NetConnection -ComputerName $p.H -Port $p.P -WarningAction SilentlyContinue
    Add-Check $p.N $(if ($t.TcpTestSucceeded) { 'PASS' } else { 'FAIL' }) "TcpTestSucceeded=$($t.TcpTestSucceeded)"
}
$fw = @(Get-NetFirewallRule -Enabled True -Direction Inbound -ErrorAction SilentlyContinue |
    Where-Object { $_.Action -eq 'Allow' } |
    Where-Object { ($_ | Get-NetFirewallPortFilter).LocalPort -contains '443' })
Add-Check '8b. Inbound-Regel fuer 443' $(if ($fw.Count -gt 0) { 'PASS' } else { 'WARN' }) `
    "$($fw.Count) aktive Allow-Regel(n) fuer TCP/443"

# ---------- 9. Reminder about the Kestrel configuration ----------
# HTTP/1.1 is only pinned when Kestrel:Https:Enabled=true and
# Authentication:Windows:Enabled=true are set (KestrelHttpsConfigurator.cs:92/138).
# A "dotnet run --urls https://..." bypasses that path, ALPN negotiates h2, and Negotiate
# fails with vague 401 responses.
$kestrelEnabled = $env:Kestrel__Https__Enabled
$winEnabled = $env:Authentication__Windows__Enabled
if ($kestrelEnabled -eq 'true' -and $winEnabled -eq 'true') {
    Add-Check '9. HTTP/1.1-Pinning' 'PASS' 'Kestrel__Https__Enabled=true und Authentication__Windows__Enabled=true in dieser Session gesetzt.'
} else {
    Add-Check '9. HTTP/1.1-Pinning' 'WARN' `
        "In DIESER Session: Kestrel__Https__Enabled='$kestrelEnabled', Authentication__Windows__Enabled='$winEnabled'. Der API-Prozess muss beides auf 'true' sehen -- sonst h2 statt HTTP/1.1."
}

# ---------- Output ----------
""
"================ PREFLIGHT npapi01 ================"
$pass = 0; $warn = 0; $fail = 0
foreach ($x in $results) {
    switch ($x.Verdict) { 'PASS' { $pass++ } 'WARN' { $warn++ } default { $fail++ } }
    "{0}  {1}  -- {2}" -f $x.Verdict, $x.Check, $x.Detail
}
""
"PASS=$pass WARN=$warn FAIL=$fail"
if ($fail -gt 0) { "FAIL vorhanden -- Testmatrix NICHT starten."; exit 1 }
