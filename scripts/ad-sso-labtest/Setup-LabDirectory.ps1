# Creates the test directory for the NodePilot Windows SSO field test (see README.md).
#
# RUN ON: dc01, or a host with RSAT AD PowerShell plus -Server dc01.
# Idempotent: a second run creates nothing twice and changes no passwords.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-LabDirectory.ps1
#
# Nesting the admins group inside the access group is the point of the test: alice is only
# a member of the admins group, so a successful login proves NodePilot reads transitive
# tokenGroups rather than memberOf alone.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Wegwerf-Lab-Credentials fuer ein isoliertes Testverzeichnis. Dasselbe Passwort muss anschliessend in Get-LabSids.ps1 und der Testsuite auftauchen -- ein SecureString wuerde diesen Ablauf nur verkomplizieren.')]
param(
    [string]$DomainDn = 'DC=np,DC=lab',
    [string]$UpnSuffix = 'np.lab',
    [string]$OuName = 'NodePilot-SsoTest',
    [string]$Server = $null,
    [string]$LabPassword = 'Lab#20260802!Kq7z',
    # Group names carry their own prefix. A lab that already connects NodePilot over LDAP
    # usually has groups with the product name, and those belong to a live configuration
    # (AllowedGroupSids / GlobalRoleMappings). This suite must never touch them.
    [string]$AccessGroup = 'NPTest-Access',
    [string]$AdminsGroup = 'NPTest-Admins',
    [string]$UserPrefix = 'nptest'
)
$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$srv = @{}
if ($Server) { $srv['Server'] = $Server }

$ouDn = "OU=$OuName,$DomainDn"
if (-not (Get-ADOrganizationalUnit -Filter "Name -eq '$OuName'" -SearchBase $DomainDn @srv -ErrorAction SilentlyContinue)) {
    New-ADOrganizationalUnit -Name $OuName -Path $DomainDn @srv
    "OU angelegt: $ouDn"
} else {
    "OU vorhanden: $ouDn"
}

# Foreign-object guard: every lookup is scoped to this OU (-SearchBase $ouDn). A domain-wide
# search would find a same-named group elsewhere, treat it as already present, and then nest
# or populate an object that belongs to a live LDAP configuration. If the name exists outside
# the OU the script aborts instead of adopting it.
foreach ($g in $AccessGroup, $AdminsGroup) {
    $inOu = Get-ADGroup -Filter "Name -eq '$g'" -SearchBase $ouDn @srv -ErrorAction SilentlyContinue
    if ($inOu) { "Gruppe vorhanden: $g"; continue }

    $elsewhere = Get-ADGroup -Filter "Name -eq '$g'" @srv -ErrorAction SilentlyContinue
    if ($elsewhere) {
        throw ("Gruppe '$g' existiert bereits ausserhalb der Test-OU ($($elsewhere.DistinguishedName)). " +
               "Diese Suite fasst fremde Gruppen nicht an -- sie koennten an einer laufenden " +
               "NodePilot-Konfiguration haengen. Anderen Namen waehlen: -AccessGroup / -AdminsGroup.")
    }
    New-ADGroup -Name $g -SamAccountName $g -GroupScope Global -GroupCategory Security -Path $ouDn @srv
    "Gruppe angelegt: $g"
}

# Nesting: the admins group is a member of the access group, so alice inherits admission
# transitively.
$accessMembers = @(Get-ADGroupMember -Identity $AccessGroup @srv | Select-Object -ExpandProperty SamAccountName)
if ($accessMembers -notcontains $AdminsGroup) {
    Add-ADGroupMember -Identity $AccessGroup -Members $AdminsGroup @srv
    "Nesting gesetzt: $AdminsGroup -> $AccessGroup"
}

# Roles of the test accounts, see the README table. bob stays without any group on purpose.
$accounts = @(
    @{ Name = "svc-$UserPrefix-dir"; Groups = @();              Zweck = 'LDAPS-Service-Bind' }
    @{ Name = "$UserPrefix.alice";   Groups = @($AdminsGroup);  Zweck = 'Happy path, transitiv -> Admin' }
    @{ Name = "$UserPrefix.carol";   Groups = @($AccessGroup);  Zweck = 'Viewer-Default' }
    @{ Name = "$UserPrefix.bob";     Groups = @();              Zweck = 'AllowedGroup-Gate (401, kein JIT)' }
    @{ Name = "$UserPrefix.dave";    Groups = @($AccessGroup);  Zweck = 'Race-Drill (bis W18 unberuehrt)' }
    @{ Name = "$UserPrefix.erin";    Groups = @($AccessGroup);  Zweck = 'Disable-/Entzugs-Drills' }
)

$secure = ConvertTo-SecureString $LabPassword -AsPlainText -Force
foreach ($a in $accounts) {
    # Scoped to the OU as well, and aborts on a name match outside it: this suite must not
    # reconfigure a foreign account or add one to its groups.
    $existing = Get-ADUser -Filter "SamAccountName -eq '$($a.Name)'" -SearchBase $ouDn @srv -ErrorAction SilentlyContinue
    if (-not $existing) {
        $foreign = Get-ADUser -Filter "SamAccountName -eq '$($a.Name)'" @srv -ErrorAction SilentlyContinue
        if ($foreign) {
            throw ("Konto '$($a.Name)' existiert bereits ausserhalb der Test-OU ($($foreign.DistinguishedName)). " +
                   "Anderes Praefix waehlen: -UserPrefix.")
        }
        # userPrincipalName is required: without a UPN the LDAP password path cannot find the
        # object. The Windows path looks up by objectSid and is unaffected, and the identity
        # test (W9) exists to surface that asymmetry.
        New-ADUser -Name $a.Name -SamAccountName $a.Name `
            -UserPrincipalName "$($a.Name)@$UpnSuffix" -Path $ouDn `
            -AccountPassword $secure -Enabled $true -PasswordNeverExpires $true @srv
        "User angelegt: $($a.Name) ($($a.Zweck))"
    } else {
        # Re-enable in case an earlier drill disabled the account.
        if (-not $existing.Enabled) { Enable-ADAccount -Identity $a.Name @srv; "User reaktiviert: $($a.Name)" }
        else { "User vorhanden: $($a.Name)" }
    }
    foreach ($g in $a.Groups) {
        $members = @(Get-ADGroupMember -Identity $g @srv | Select-Object -ExpandProperty SamAccountName)
        if ($members -notcontains $a.Name) {
            Add-ADGroupMember -Identity $g -Members $a.Name @srv
            "  -> $($a.Name) in $g aufgenommen"
        }
    }
}

""
"Verzeichnis bereit. SIDs jetzt mit Get-LabSids.ps1 auslesen -- die Domain-SID ist pro"
"Lab neu, hartkodierte SIDs waeren nach einer Neuprovisionierung falsch."
