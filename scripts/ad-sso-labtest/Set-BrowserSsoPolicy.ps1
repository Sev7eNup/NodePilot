# Setzt (bzw. entfernt) die Browser-Policy, die den Negotiate-Handshake fuer die
# NodePilot-Origin freigibt. AUSFUEHREN AUF: npcli01, elevated.
#
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File .\Set-BrowserSsoPolicy.ps1
#         .\Set-BrowserSsoPolicy.ps1 -Remove
#
# Im Produktivbetrieb kommt das per GPO. Hier direkt in die Policy-Hives, damit das Lab
# ohne GPO-Refresh-Warterei testbar bleibt.
#
# Bewusst NICHT gesetzt:
#   * AuthNegotiateDelegateAllowlist -- Delegation ist fuer NodePilot weder noetig noch
#     erwuenscht (kein Double-Hop im Auth-Pfad).
#   * AuthSchemes -- der Browser ist der falsche Ort, um NTLM zu blocken; SPNEGO kann NTLM
#     auch unter "negotiate" transportieren. Dafuer ist die Restrict-NTLM-GPO zustaendig
#     (siehe README, Phase 2).
param(
    [string]$ApiFqdn = 'npapi01.np.lab',
    [ValidateSet('http', 'https')]
    [string]$Scheme = 'https',
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

$policyPaths = @(
    @{ Name = 'Edge';   Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' }
    @{ Name = 'Chrome'; Path = 'HKLM:\SOFTWARE\Policies\Google\Chrome' }
)

# ZoneMap: 1 = Local Intranet. "Automatische Anmeldung nur in der Intranetzone" ist dort
# der Default, deshalb reicht die Zonenzuordnung.
$hostLabel, $domainLabel = $ApiFqdn.Split('.', 2)
$zonePath = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Domains\$domainLabel\$hostLabel"

if ($Remove) {
    foreach ($p in $policyPaths) {
        if (Test-Path $p.Path) {
            Remove-ItemProperty -Path $p.Path -Name 'AuthServerAllowlist' -ErrorAction SilentlyContinue
            "AuthServerAllowlist entfernt: $($p.Name)"
        }
    }
    if (Test-Path $zonePath) {
        Remove-Item -Path $zonePath -Recurse -Force
        "ZoneMap-Eintrag entfernt: $zonePath"
    }
    ""
    "Browser komplett neu starten, damit die Policy verschwindet."
    return
}

foreach ($p in $policyPaths) {
    if (-not (Test-Path $p.Path)) { New-Item -Path $p.Path -Force | Out-Null }
    New-ItemProperty -Path $p.Path -Name 'AuthServerAllowlist' -Value $ApiFqdn -PropertyType String -Force | Out-Null
    "AuthServerAllowlist=$ApiFqdn gesetzt: $($p.Name)"
}

if (-not (Test-Path $zonePath)) { New-Item -Path $zonePath -Force | Out-Null }
New-ItemProperty -Path $zonePath -Name $Scheme -Value 1 -PropertyType DWord -Force | Out-Null
# ${Scheme} muss geklammert werden -- "$Scheme://" liest PowerShell sonst als
# scope-qualifizierte Variable ("$Scheme:" + Rest).
"ZoneMap: ${Scheme}://$ApiFqdn -> Zone 1 (Local Intranet)"

""
"Naechste Schritte:"
"  1. Edge und Chrome KOMPLETT schliessen (alle Fenster) und neu starten."
"  2. edge://policy bzw. chrome://policy oeffnen -- AuthServerAllowlist muss als"
"     'Applied' erscheinen. Ein Screenshot davon gehoert in die Evidenz."
"  3. ${Scheme}://$ApiFqdn/login aufrufen und 'Windows-Anmeldung' klicken."
""
"Hinweis: Ein erfolgreiches 'Invoke-WebRequest -UseDefaultCredentials' beweist Kerberos"
"auf dem HTTP-Stack, NICHT die Browser-Policy. Beide Nachweise sind noetig."
