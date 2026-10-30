# Shared identity defaults for provisioning, probes, and database evidence.
function Resolve-NodePilotLabIdentity {
    param(
        [string]$UserPrefix,
        [string]$OuName,
        [string]$DomainDn = 'DC=np,DC=lab',
        [string]$AccessGroup,
        [string]$AdminsGroup
    )
    if ([string]::IsNullOrWhiteSpace($UserPrefix)) { $UserPrefix = 'nptest' }
    if ($UserPrefix -notmatch '^[A-Za-z0-9-]{1,12}$') {
        throw 'UserPrefix must contain 1-12 ASCII letters, digits, or hyphens (sAMAccountName limit).'
    }
    if ([string]::IsNullOrWhiteSpace($OuName)) { $OuName = 'NodePilot-SsoTest' }
    if ([string]::IsNullOrWhiteSpace($AccessGroup)) { $AccessGroup = 'NPTest-Access' }
    if ([string]::IsNullOrWhiteSpace($AdminsGroup)) { $AdminsGroup = 'NPTest-Admins' }
    [pscustomobject]@{
        UserPrefix = $UserPrefix
        OuName = $OuName
        AccessGroup = $AccessGroup
        AdminsGroup = $AdminsGroup
        ServiceBindDn = "CN=svc-$UserPrefix-dir,OU=$OuName,$DomainDn"
        Alice = "$UserPrefix.alice"
        Bob = "$UserPrefix.bob"
        Dave = "$UserPrefix.dave"
        Erin = "$UserPrefix.erin"
    }
}
