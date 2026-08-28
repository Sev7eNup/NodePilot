# Reads the group and user SIDs of the test directory and prints a copy-paste ready
# env var block for PHASE B of the API (see README.md, section "PHASE B").
#
# RUN ON: dc01 or a host with RSAT-AD-PowerShell (then pass -Server dc01.np.lab).
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\Get-LabSids.ps1
#        .\Get-LabSids.ps1 -AsEnvBlock | Set-Clipboard
#
# The domain SID is new for every lab provisioning, so never hardcode SIDs; read them
# fresh before each test run.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Das Service-Bind-Passwort wird bewusst im Klartext in den Env-Block gerendert -- genau so muss es der API-Prozess in PHASE B sehen.')]
param(
    [string]$DomainDn = 'DC=np,DC=lab',
    [string]$UpnSuffix = 'np.lab',
    [string]$OuName = 'NodePilot-SsoTest',
    [string]$DcFqdn = 'dc01.np.lab',
    [string]$Server = $null,
    [string]$ServicePassword = 'Lab#20260802!Kq7z',
    # Must match Setup-LabDirectory.ps1. The prefix is separate so that existing NodePilot
    # groups of a live LDAP binding stay untouched.
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
# Read scoped to the OU: in a lab with an existing NodePilot LDAP binding a group of the
# same name may live elsewhere, and writing its SID into the test configuration would
# silently point at foreign objects.
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
