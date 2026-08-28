# Sets (or removes) the browser policy that allows the Negotiate handshake for the
# NodePilot origin. RUN ON: npcli01, elevated.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\Set-BrowserSsoPolicy.ps1
#        .\Set-BrowserSsoPolicy.ps1 -Remove
#
# In production this comes from a GPO. Here the policy hives are written directly so the
# lab stays testable without waiting for a GPO refresh.
#
# Deliberately not set:
#   * AuthNegotiateDelegateAllowlist -- delegation is neither needed nor wanted for
#     NodePilot (no double hop in the auth path).
#   * AuthSchemes -- the browser is the wrong place to block NTLM; SPNEGO can carry NTLM
#     under "negotiate" as well. The Restrict NTLM GPO covers that
#     (see README, phase 2).
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

# ZoneMap: 1 = Local Intranet. Automatic logon only in the intranet zone is the default
# there, so the zone assignment is enough.
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
# ${Scheme} needs the braces: PowerShell otherwise reads "$Scheme://" as a scope-qualified
# variable ("$Scheme:" plus the rest).
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
