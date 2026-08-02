# Liest die Gruppen-/User-SIDs des Testverzeichnisses und gibt einen copy-paste-fertigen
# Env-Var-Block fuer PHASE B der API aus (siehe README.md, Abschnitt "PHASE B").
#
# AUSFUEHREN AUF: dc01 oder einem Host mit RSAT-AD-PowerShell (dann -Server dc01.np.lab).
#
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File .\Get-LabSids.ps1
#         .\Get-LabSids.ps1 -AsEnvBlock | Set-Clipboard
#
# Die Domain-SID ist pro Lab-Provisionierung neu. SIDs deshalb NIE hartkodieren, sondern
# vor jedem Testlauf frisch auslesen.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Das Service-Bind-Passwort wird bewusst im Klartext in den Env-Block gerendert -- genau so muss es der API-Prozess in PHASE B sehen.')]
param(
    [string]$DomainDn = 'DC=np,DC=lab',
    [string]$UpnSuffix = 'np.lab',
    [string]$OuName = 'NodePilot-SsoTest',
    [string]$DcFqdn = 'dc01.np.lab',
    [string]$Server = $null,
    [string]$ServicePassword = 'Lab#20260802!Kq7z',
    # Muessen zu Setup-LabDirectory.ps1 passen -- eigenes Praefix, damit vorhandene
    # NodePilot-Gruppen einer laufenden LDAP-Anbindung unberuehrt bleiben.
    [string]$AccessGroup = 'NPTest-Access',
    [string]$AdminsGroup = 'NPTest-Admins',
    [string]$UserPrefix = 'nptest',
    [switch]$AsEnvBlock
)
$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$srv = @{}
if ($Server) { $srv['Server'] = $Server }

$ouDn = "OU=$OuName,$DomainDn"
# OU-gescopet lesen: in einem Lab mit bestehender NodePilot-LDAP-Anbindung koennte eine
# gleichnamige Gruppe anderswo stehen, und deren SID in die Testkonfiguration zu schreiben
# waere ein stiller Fehlgriff auf fremde Objekte.
$accessSid = (Get-ADGroup -Filter "Name -eq '$AccessGroup'" -SearchBase $ouDn -Properties objectSid @srv).SID.Value
$adminsSid = (Get-ADGroup -Filter "Name -eq '$AdminsGroup'" -SearchBase $ouDn -Properties objectSid @srv).SID.Value
if (-not $accessSid -or -not $adminsSid) {
    throw "Gruppen '$AccessGroup'/'$AdminsGroup' nicht in $ouDn gefunden -- erst Setup-LabDirectory.ps1 mit denselben Parametern laufen lassen."
}

if (-not $AsEnvBlock) {
    "Gruppen-SIDs (aus $ouDn):"
    "  {0,-16} = {1}   (AllowedGroupSids -- Admission-Gate)" -f $AccessGroup, $accessSid
    "  {0,-16} = {1}   (GlobalRoleMappings -> Admin)" -f $AdminsGroup, $adminsSid
    ""
    "User-SIDs (Subject in ExternalIdentities; W9 vergleicht dagegen):"
    foreach ($u in "$UserPrefix.alice", "$UserPrefix.carol", "$UserPrefix.bob", "$UserPrefix.dave", "$UserPrefix.erin") {
        $sid = (Get-ADUser -Filter "SamAccountName -eq '$u'" -SearchBase $ouDn -Properties objectSid @srv).SID.Value
        "  {0,-16} = {1}" -f $u, $sid
    }
    ""
    "Env-Block fuer PHASE B (erneut mit -AsEnvBlock aufrufen):"
    ""
}

$serviceBindDn = "CN=svc-$UserPrefix-dir,OU=$OuName,$DomainDn"
@"
`$env:Authentication__LocalLoginMode                        = 'BreakGlassOnly'
`$env:Authentication__MaxAuthorizationStalenessMinutes      = '15'
`$env:Authentication__Ldap__Enabled                         = 'true'
`$env:Authentication__Ldap__Endpoints__0                    = '${DcFqdn}:636'
`$env:Authentication__Ldap__Port                            = '636'
`$env:Authentication__Ldap__UseSsl                          = 'true'
`$env:Authentication__Ldap__BaseDn                          = '$DomainDn'
`$env:Authentication__Ldap__UpnSuffix                       = '$UpnSuffix'
`$env:Authentication__Ldap__BindTimeoutSeconds              = '5'
`$env:Authentication__Ldap__ServiceBindDn                   = '$serviceBindDn'
`$env:Authentication__Ldap__ServicePassword                 = '$ServicePassword'
`$env:Authentication__Ldap__AllowedGroupSids__0             = '$accessSid'
`$env:Authentication__Ldap__GlobalRoleMappings__0__GroupSid = '$adminsSid'
`$env:Authentication__Ldap__GlobalRoleMappings__0__Role     = 'Admin'
`$env:Authentication__Ldap__DirectorySyncIntervalMinutes    = '1'
`$env:Authentication__Windows__Enabled                      = 'true'
`$env:Authentication__Windows__AllowNtlmFallback            = 'false'
`$env:Authentication__Windows__NtlmDisabledByPolicy         = 'true'
"@
