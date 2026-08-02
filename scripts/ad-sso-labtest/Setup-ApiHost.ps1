# Preflight fuer den NodePilot-API-Member-Server (npapi01) vor dem Windows-SSO-Feldtest.
#
# AUSFUEHREN AUF: npapi01, elevated (setspn/Zertifikatsstore/Firewall brauchen Rechte).
# Read-only -- das Skript aendert NICHTS, es prueft nur und meldet PASS/WARN/FAIL.
#
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-ApiHost.ps1 `
#             -ApiFqdn npapi01.np.lab -NtlmAliasFqdn npapi01-ntlm.np.lab -DcFqdn dc01.np.lab
#
# Jeder FAIL hier erzeugt spaeter ein Kerberos-Fehlerbild, das wie ein Produktdefekt
# aussieht. Der Preflight muss gruen sein, BEVOR die Testmatrix laeuft.
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

# ---------- 1. Domain-Join ----------
# Ohne Domain-Join hat der Prozess keinen Schluessel zum Entschluesseln des Service-Tickets.
# ASP.NET Negotiate nutzt auf Windows SSPI -- ein Keytab-Ersatz existiert hier nicht.
$cs = Get-CimInstance Win32_ComputerSystem
if ($cs.PartOfDomain) {
    Add-Check '1. Domain-Join' 'PASS' "Domain=$($cs.Domain) Host=$($cs.Name)"
} else {
    Add-Check '1. Domain-Join' 'FAIL' 'Host ist NICHT domaenengejoint -- Negotiate kann nicht funktionieren.'
}

# ---------- 2. Zeitversatz gegen den DC ----------
# Kerberos toleriert +/-5 min. Ein Versatz darueber liefert KRB_AP_ERR_SKEW und sieht
# im Fehlerbild aus wie "SPN falsch".
try {
    $chart = & w32tm /stripchart /computer:$DcFqdn /samples:3 /dataonly 2>&1 | Out-String
    # w32tm gibt den Offset auch auf de-DE mit PUNKT als Dezimaltrenner aus ("-00.0010514s").
    # Der PowerShell-Cast [double] konvertiert invariant und ist damit korrekt.
    # NICHT auf [double]::Parse() umbauen: das nutzt die aktuelle Kultur und liest unter
    # de-DE "-00.0010514" als -10514 -- aus 1 ms Versatz wuerde ein Fehlalarm von 3 Stunden.
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

# ---------- 3. DNS: API-FQDN und NTLM-Alias zeigen auf DIESEN Host ----------
# Der Alias muss dieselbe IP liefern (gleiches Kestrel), darf aber keinen SPN haben --
# das ist der deterministische NTLM-Ausloeser fuer W19.
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

# CNAME-Falle: Chromium kanonisiert den Host und bildet den SPN aus dem A-Record-Ziel.
# Ein CNAME auf einen anderen Namen erzeugt damit einen SPN, den niemand registriert hat.
foreach ($f in @($ApiFqdn, $NtlmAliasFqdn)) {
    $cname = @(Resolve-DnsName -Name $f -Type CNAME -ErrorAction SilentlyContinue |
        Where-Object QueryType -eq 'CNAME')
    if ($cname.Count -gt 0) {
        Add-Check "3c. CNAME-Kanonisierung ($f)" 'WARN' `
            "$f ist ein CNAME auf $($cname[0].NameHost) -- Chromium bildet den SPN aus dem A-Record-Ziel. Im Lab direkten A-Record verwenden."
    }
}

# ---------- 4. SPN-Lage ----------
# Variante A (LocalSystem/Maschinenkonto): HOST/<fqdn> deckt HTTP/ implizit ueber
# sPNMappings ab -- ein EXPLIZITER HTTP/-SPN auf einem anderen Konto gewinnt dagegen und
# bricht den Handshake. Variante B (gMSA): genau ein expliziter Treffer ist erwuenscht.
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

# Der NTLM-Alias MUSS SPN-frei bleiben, sonst faellt W19 nicht mehr auf NTLM zurueck.
$aliasQuery = (& setspn -Q "HTTP/$NtlmAliasFqdn" 2>&1 | Out-String)
$aliasHits = @([regex]::Matches($aliasQuery, '(?im)^\s*CN=.+$'))
Add-Check '4c. NTLM-Alias ist SPN-frei' $(if ($aliasHits.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($aliasHits.Count -eq 0) { "Kein HTTP/$NtlmAliasFqdn registriert -- NTLM-Fallback ist deterministisch." } else { "SPN vorhanden -- W19 wuerde Kerberos statt NTLM ausloesen." })

$dup = (& setspn -X 2>&1 | Out-String)
Add-Check '4d. Domaenenweite SPN-Duplikate' $(if ($dup -match 'found 0 group') { 'PASS' } else { 'WARN' }) `
    (($dup -split "`n" | Where-Object { $_ -match 'duplicate|found \d+ group' } | Select-Object -First 2) -join ' | ')

# ---------- 5. Verschluesselungstypen ----------
# RC4-only-Konto plus AES-erzwingende Domain-Policy ergibt KDC_ERR_ETYPE_NOSUPP.
try {
    $etypes = (Get-ADComputer $cs.Name -Properties 'msDS-SupportedEncryptionTypes' -Server $DcFqdn -ErrorAction Stop).'msDS-SupportedEncryptionTypes'
    # Bit 3 (0x08) = AES128, Bit 4 (0x10) = AES256.
    $hasAes = ($null -ne $etypes) -and (($etypes -band 0x18) -ne 0)
    Add-Check '5. Kerberos-Verschluesselungstypen' $(if ($hasAes -or $null -eq $etypes) { 'PASS' } else { 'WARN' }) `
        "msDS-SupportedEncryptionTypes=$etypes (AES=$hasAes; leer = Domain-Default)"
} catch {
    Add-Check '5. Kerberos-Verschluesselungstypen' 'WARN' "Nicht lesbar (RSAT fehlt?): $($_.Exception.Message)"
}

# ---------- 6. Kestrel-Zertifikat: SAN muss BEIDE Namen tragen ----------
# Eine einzige Kette: URL-Host = SPN-Host = Zertifikats-SAN. Der NTLM-Alias braucht die
# zweite SAN, damit W19 kein Zertifikatswarnungs-Interstitial produziert.
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

# ---------- 7. LDAPS mit scharfer Zertifikatsvalidierung ----------
# Spiegelt exakt das, was SystemLdapConnectionAdapter tut: Port 636, SSL, KEIN
# Validierungs-Callback -- die DC-Kette muss im LocalMachine\Root des API-Hosts liegen.
# Es gibt dafuer keinen In-App-Bypass.
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
    # Fehler 81 ("server unavailable") maskiert auch Zertifikatsprobleme -- deshalb der
    # explizite Hinweis statt einer nackten Exception.
    Add-Check '7. LDAPS-Bind + BaseDn-Read' 'FAIL' `
        "$($_.Exception.Message) | Bei LdapException 81: DC-Zertifikat/SAN, CA-Trust im LocalMachine\Root, FQDN statt IP pruefen."
}

# ---------- 8. Firewall / Port-Erreichbarkeit ----------
foreach ($p in @(@{ N = '8a. LDAPS 636 zum DC'; H = $DcFqdn; P = 636 })) {
    $t = Test-NetConnection -ComputerName $p.H -Port $p.P -WarningAction SilentlyContinue
    Add-Check $p.N $(if ($t.TcpTestSucceeded) { 'PASS' } else { 'FAIL' }) "TcpTestSucceeded=$($t.TcpTestSucceeded)"
}
$fw = @(Get-NetFirewallRule -Enabled True -Direction Inbound -ErrorAction SilentlyContinue |
    Where-Object { $_.Action -eq 'Allow' } |
    Where-Object { ($_ | Get-NetFirewallPortFilter).LocalPort -contains '443' })
Add-Check '8b. Inbound-Regel fuer 443' $(if ($fw.Count -gt 0) { 'PASS' } else { 'WARN' }) `
    "$($fw.Count) aktive Allow-Regel(n) fuer TCP/443"

# ---------- 9. Erinnerung an die Kestrel-Konfiguration ----------
# HTTP/1.1 wird nur gepinnt, wenn Kestrel:Https:Enabled=true UND
# Authentication:Windows:Enabled=true gesetzt sind (KestrelHttpsConfigurator.cs:92/138).
# Ein "dotnet run --urls https://..." umgeht den Pfad, ALPN handelt h2 aus, und Negotiate
# bricht mit diffusen 401.
$kestrelEnabled = $env:Kestrel__Https__Enabled
$winEnabled = $env:Authentication__Windows__Enabled
if ($kestrelEnabled -eq 'true' -and $winEnabled -eq 'true') {
    Add-Check '9. HTTP/1.1-Pinning' 'PASS' 'Kestrel__Https__Enabled=true und Authentication__Windows__Enabled=true in dieser Session gesetzt.'
} else {
    Add-Check '9. HTTP/1.1-Pinning' 'WARN' `
        "In DIESER Session: Kestrel__Https__Enabled='$kestrelEnabled', Authentication__Windows__Enabled='$winEnabled'. Der API-Prozess muss beides auf 'true' sehen -- sonst h2 statt HTTP/1.1."
}

# ---------- Ausgabe ----------
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
