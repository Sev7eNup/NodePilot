# Legt das Testverzeichnis fuer den NodePilot Windows-SSO-Feldtest an (siehe README.md).
#
# AUSFUEHREN AUF: dc01 (oder einem Host mit RSAT-AD-PowerShell und -Server dc01).
# Idempotent -- ein zweiter Lauf legt nichts doppelt an und aendert keine Passwoerter.
#
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File .\Setup-LabDirectory.ps1
#
# Das Nesting NodePilot-Admins IN NodePilot-Access ist der Kern des Tests: alice ist NUR
# in -Admins. Ein Login als alice beweist damit, dass NodePilot transitive tokenGroups
# liest und nicht bloss memberOf.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '',
    Justification = 'Wegwerf-Lab-Credentials fuer ein isoliertes Testverzeichnis. Dasselbe Passwort muss anschliessend in Get-LabSids.ps1 und der Testsuite auftauchen -- ein SecureString wuerde diesen Ablauf nur verkomplizieren.')]
param(
    [string]$DomainDn = 'DC=np,DC=lab',
    [string]$UpnSuffix = 'np.lab',
    [string]$OuName = 'NodePilot-SsoTest',
    [string]$Server = $null,
    [string]$LabPassword = 'Lab#20260802!Kq7z',
    # Gruppennamen bewusst mit eigenem Praefix: in einem Lab, in dem NodePilot schon per
    # LDAP angebunden ist, existieren typischerweise bereits Gruppen wie "NodePilot-Users"
    # oder "NodePilot-Admins". Diese Suite darf sie NIE anfassen -- sie haengen an einer
    # laufenden Konfiguration (AllowedGroupSids / GlobalRoleMappings).
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

# Fremdobjekt-Schutz: JEDE Suche wird auf die eigene OU begrenzt (-SearchBase $ouDn).
# Ohne das findet eine domainweite Namenssuche eine gleichnamige Gruppe an anderer Stelle,
# haelt sie fuer "schon vorhanden" und verschachtelt bzw. befuellt anschliessend ein Objekt,
# das zu einer laufenden LDAP-Konfiguration gehoert. Existiert der Name ausserhalb der OU,
# bricht das Skript ab statt ihn zu adoptieren.
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

# Nesting: Admins ist Mitglied von Access. alice erbt die Admission dadurch transitiv.
$accessMembers = @(Get-ADGroupMember -Identity $AccessGroup @srv | Select-Object -ExpandProperty SamAccountName)
if ($accessMembers -notcontains $AdminsGroup) {
    Add-ADGroupMember -Identity $AccessGroup -Members $AdminsGroup @srv
    "Nesting gesetzt: $AdminsGroup -> $AccessGroup"
}

# Rollen der Testkonten -- siehe README-Tabelle. bob bleibt bewusst gruppenlos.
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
    # Auch hier OU-gescopet plus Abbruch bei Namensgleichheit ausserhalb -- ein fremdes
    # Konto darf diese Suite weder umkonfigurieren noch in ihre Gruppen aufnehmen.
    $existing = Get-ADUser -Filter "SamAccountName -eq '$($a.Name)'" -SearchBase $ouDn @srv -ErrorAction SilentlyContinue
    if (-not $existing) {
        $foreign = Get-ADUser -Filter "SamAccountName -eq '$($a.Name)'" @srv -ErrorAction SilentlyContinue
        if ($foreign) {
            throw ("Konto '$($a.Name)' existiert bereits ausserhalb der Test-OU ($($foreign.DistinguishedName)). " +
                   "Anderes Praefix waehlen: -UserPrefix.")
        }
        # userPrincipalName ist Pflicht: ohne UPN findet der LDAP-Passwortpfad das Objekt
        # nicht. Der Windows-Pfad sucht per objectSid und waere nicht betroffen -- genau
        # diese Asymmetrie macht der Identitaetstest (W9) sichtbar.
        New-ADUser -Name $a.Name -SamAccountName $a.Name `
            -UserPrincipalName "$($a.Name)@$UpnSuffix" -Path $ouDn `
            -AccountPassword $secure -Enabled $true -PasswordNeverExpires $true @srv
        "User angelegt: $($a.Name) ($($a.Zweck))"
    } else {
        # Wieder aktivieren, falls ein frueherer Drill das Konto deaktiviert hat.
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
