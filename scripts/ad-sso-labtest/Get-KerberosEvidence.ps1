# Sammelt die Kerberos-/NTLM-Belege, die der Feldtest als Evidenz braucht, und legt sie
# unter .\evidence\<timestamp>\ ab (gitignored).
#
# AUSFUEHREN AUF:
#   npcli01  -> klist tickets, Browser-Policy (Client-Sicht)
#   npapi01  -> Security-4624, NTLM-Operational (Server-Sicht)
# Beide Seiten laufen lassen; -Role steuert, was gesammelt wird.
#
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File .\Get-KerberosEvidence.ps1 -Role Client
#         .\Get-KerberosEvidence.ps1 -Role Server -SinceMinutes 30
#
# Warum ueberhaupt: ein Handshake, der auf NTLM zurueckfaellt, liefert HTTP 200 genauso
# wie Kerberos, solange die Policy NTLM noch erlaubt. Ohne diese Belege ist "SSO
# funktioniert" nicht von "NTLM funktioniert" zu unterscheiden.
param(
    [ValidateSet('Client', 'Server', 'Both')]
    [string]$Role = 'Both',
    [string]$ApiFqdn = 'npapi01.np.lab',
    [int]$SinceMinutes = 30,
    [string]$OutputRoot = $null
)
$ErrorActionPreference = 'Continue'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $scriptDir 'evidence' }
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$dir = Join-Path $OutputRoot $stamp
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$since = (Get-Date).AddMinutes(-$SinceMinutes)

"Evidenzverzeichnis: $dir"
"Zeitfenster       : ab $($since.ToString('yyyy-MM-dd HH:mm:ss'))"
""

function Save-Evidence([string]$file, [string[]]$lines) {
    $path = Join-Path $dir $file
    $lines | Out-File -FilePath $path -Encoding utf8
    "  geschrieben: $file ($($lines.Count) Zeilen)"
}

if ($Role -in @('Client', 'Both')) {
    "Client-Belege:"

    # Das Service-Ticket ist der harte Kerberos-Beweis. Ein NTLM-Fallback hinterlaesst
    # hier keinen HTTP/-Eintrag.
    $tickets = @(& klist tickets 2>&1 | ForEach-Object { "$_" })
    Save-Evidence 'klist-tickets.txt' $tickets
    $hasSvc = ($tickets -join "`n") -match [regex]::Escape("HTTP/$ApiFqdn")
    "  Service-Ticket HTTP/$ApiFqdn : $hasSvc"

    Save-Evidence 'klist-tgt.txt' @(& klist tgt 2>&1 | ForEach-Object { "$_" })

    # Browser-Policy: NICHT nur dumpen, sondern bewerten. Fehlt die Allowlist fuer den
    # geprueften FQDN, fragt der Browser nach Credentials statt still das Ticket vorzuweisen
    # -- und genau dann ist ein "stiller" Login nur der Credential-Cache oder ein Filler.
    # Ohne diese Bewertung stand die Information zwar in der Evidenz, wurde aber uebersehen.
    $policy = @()
    $allowlisted = $false
    foreach ($p in @('HKLM:\SOFTWARE\Policies\Microsoft\Edge', 'HKLM:\SOFTWARE\Policies\Google\Chrome')) {
        if (Test-Path $p) {
            $v = (Get-ItemProperty -Path $p -ErrorAction SilentlyContinue).AuthServerAllowlist
            $policy += "$p : AuthServerAllowlist = $v"
            if ($v -and ($v -split '\s*,\s*') -contains $ApiFqdn) { $allowlisted = $true }
        } else {
            $policy += "$p : nicht vorhanden"
        }
    }
    $zoneRoot = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Domains'
    $zoneMapped = $false
    if (Test-Path $zoneRoot) {
        $entries = @(Get-ChildItem -Path $zoneRoot -Recurse -ErrorAction SilentlyContinue)
        $policy += ($entries | ForEach-Object { "ZoneMap: $($_.PSPath -replace '.*ZoneMap\\', '')" })
        $hostLabel, $domainLabel = $ApiFqdn.Split('.', 2)
        $zoneMapped = [bool]($entries | Where-Object { $_.PSPath -match [regex]::Escape("$domainLabel\$hostLabel") })
    } else {
        $policy += 'ZoneMap: keine Policy-Eintraege vorhanden'
    }

    # Persistente Anmeldeinformationen maskieren eine fehlende Policy komplett.
    $stored = @(& cmdkey /list 2>&1 | Select-String -Pattern ([regex]::Escape($ApiFqdn)))
    $policy += ''
    $policy += "cmdkey-Eintraege fuer ${ApiFqdn}: $(if ($stored.Count) { $stored.Count } else { 0 })"
    $policy += $stored | ForEach-Object { "  $($_.Line.Trim())" }

    $verdict = if ($allowlisted -or $zoneMapped) { 'OK' } else { 'WARNUNG' }
    $policy += ''
    $policy += "Bewertung: $verdict (Allowlist=$allowlisted ZoneMap=$zoneMapped)"
    Save-Evidence 'browser-policy.txt' $policy

    if ($verdict -eq 'OK') {
        "  Browser-Policy fuer $ApiFqdn : OK (Allowlist=$allowlisted ZoneMap=$zoneMapped)"
    } else {
        "  WARNUNG: weder AuthServerAllowlist noch Intranet-ZoneMap fuer $ApiFqdn gesetzt."
        "           Der Browser wird nach Credentials FRAGEN. Ein trotzdem stiller Login"
        "           kommt dann aus dem Credential-Cache, dem Windows-Tresor oder einem"
        "           ESSO-Filler -- und beweist KEIN Single Sign-On (siehe W6b/W6c)."
    }
    if ($stored.Count -gt 0) {
        "  WARNUNG: $($stored.Count) gespeicherte Anmeldeinformation(en) fuer $ApiFqdn."
        "           Der Prompt-Freiheits-Test ist damit wertlos, bis sie entfernt sind"
        "           (cmdkey /delete:<Ziel>). Hinweis: aus einer PowerShell-Direct- oder"
        "           WinRM-Sitzung ist der Tresor des Desktop-Benutzers nicht vollstaendig"
        "           sichtbar -- an der Konsole gegenpruefen."
    }
    ""
}

if ($Role -in @('Server', 'Both')) {
    "Server-Belege:"

    # 4624 LogonType 3 ist der serverseitige Gegenbeweis zum klist-Ticket.
    #
    # Ausgewertet werden die strukturierten EventData-Felder, NICHT der Message-Text:
    # der ist lokalisiert (auf de-DE steht dort "Anmeldetyp"/"Authentifizierungspaket"),
    # ein Text-Regex faende auf einem deutschen Server null Treffer und saehe damit aus
    # wie "kein Kerberos-Logon".
    #
    # Die Feldkombination ist das eigentliche Signal:
    #   AuthenticationPackageName=Kerberos                      -> echtes Kerberos
    #   AuthenticationPackageName=Negotiate + LmPackageName=NTLM -> SPNEGO auf NTLM
    #                                                              zurueckgefallen
    # Ein reiner Blick auf "Negotiate" wuerde den Fallback also gerade verdecken.
    try {
        $rows = @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4624; StartTime = $since } -ErrorAction Stop |
            ForEach-Object {
                $d = @{}
                foreach ($f in ([xml]$_.ToXml()).Event.EventData.Data) { $d[$f.Name] = $f.'#text' }
                [pscustomobject]@{
                    Time    = $_.TimeCreated
                    Type    = $d['LogonType']
                    Package = $d['AuthenticationPackageName']
                    Lm      = $d['LmPackageName']
                    Account = "$($d['TargetDomainName'])\$($d['TargetUserName'])"
                    Ip      = $d['IpAddress']
                }
            } | Where-Object { $_.Type -eq '3' } | Select-Object -First 50)

        Save-Evidence 'security-4624-network-logons.txt' @(
            'Zeit                 Package    LmPackage   Konto                     IP'
            $rows | ForEach-Object {
                '{0:yyyy-MM-dd HH:mm:ss}  {1,-10} {2,-11} {3,-25} {4}' -f $_.Time, $_.Package, $_.Lm, $_.Account, $_.Ip
            }
        )
        $kerb = @($rows | Where-Object { $_.Package -eq 'Kerberos' }).Count
        $ntlm = @($rows | Where-Object { $_.Package -eq 'NTLM' -or $_.Lm -like 'NTLM*' }).Count
        "  Netzwerk-Logons im Fenster: Kerberos=$kerb NTLM(inkl. Negotiate-Fallback)=$ntlm"
    } catch {
        "  Security-Log nicht lesbar (elevated ausfuehren?): $($_.Exception.Message)"
    }

    # 8004 = eingehendes NTLM im Auditmodus protokolliert (W19).
    # 4004 = eingehendes NTLM durch "Deny all accounts" blockiert (W20).
    try {
        $ntlmEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-NTLM/Operational'; StartTime = $since } -ErrorAction Stop |
            Select-Object -First 50 |
            ForEach-Object { "{0:yyyy-MM-dd HH:mm:ss}  Id={1}  {2}" -f $_.TimeCreated, $_.Id, ($_.Message -replace '\s+', ' ') })
        Save-Evidence 'ntlm-operational.txt' $ntlmEvents
        "  NTLM-Events: 8004(audit)=$(@($ntlmEvents | Where-Object { $_ -match 'Id=8004' }).Count) 4004(blocked)=$(@($ntlmEvents | Where-Object { $_ -match 'Id=4004' }).Count)"
    } catch {
        "  NTLM-Operational-Log leer oder nicht aktiviert: $($_.Exception.Message)"
    }

    # Registry-Beleg fuer die NtlmDisabledByPolicy-Attestierung.
    $lsa = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0'
    $restrict = (Get-ItemProperty -Path $lsa -ErrorAction SilentlyContinue).RestrictReceivingNTLMTraffic
    Save-Evidence 'ntlm-policy-registry.txt' @(
        "RestrictReceivingNTLMTraffic = $restrict"
        '0/leer = erlaubt, 1 = Audit, 2 = Deny all accounts'
        'Nur bei 2 ist Authentication:Windows:NtlmDisabledByPolicy=true belegt.'
    )
    "  RestrictReceivingNTLMTraffic = $restrict (2 = Deny all accounts)"

    Save-Evidence 'spn-state.txt' @(
        "setspn -Q HTTP/${ApiFqdn}:"
        (& setspn -Q "HTTP/$ApiFqdn" 2>&1 | ForEach-Object { "$_" })
        ''
        'setspn -X (Duplikate):'
        (& setspn -X 2>&1 | ForEach-Object { "$_" })
    )
    ""
}

""
"Fertig. Zusaetzlich manuell in $dir ablegen:"
"  * Fiddler-Session (.saz) mit beiden Negotiate-Legs: 401 WWW-Authenticate: Negotiate"
"    gefolgt von 200 mit Authorization: Negotiate YII... (SPNEGO, nicht TlRMTVNTUA==)."
"  * Screenshot von edge://policy mit AuthServerAllowlist = Applied."
"  * Screenshot des eingeloggten NodePilot-Dashboards."
"  * Dekodierter JWT-Payload aus dem np_auth-Cookie (ohne Gruppen-Claims)."
