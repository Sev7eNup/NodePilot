#requires -Version 5.1
<# Offline behavioral checks: execute the updater's rollback statements with fake services,
   database tools and isolated fixture files. No installer, database or service is touched. #>
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'desktop\Update-Desktop.ps1'
$parseErrors = $null
$tokens = $null
$program = [System.Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Updater contains PowerShell syntax errors: $parseErrors" }
$update = @($program.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] })[-1]
$rollbackBody = $update.CatchClauses[0].Body
$rollbackText = $rollbackBody.Extent.Text.Substring(1, $rollbackBody.Extent.Text.Length - 2)
# Return to this harness where production exits its process; preserve every branch/statement.
foreach ($exit in @($rollbackBody.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.ExitStatementAst]
}, $true) | Sort-Object { $_.Extent.StartOffset } -Descending)) {
    $offset = $exit.Extent.StartOffset - $rollbackBody.Extent.StartOffset - 1
    $rollbackText = $rollbackText.Remove($offset, $exit.Extent.Text.Length).Insert($offset, 'return')
}
$rollback = [scriptblock]::Create($rollbackText)

# Only import the restore helper when present. All OS-facing commands are replaced below.
$restoreFunction = $program.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Restore-DatabaseBackup'
}, $false)
if ($restoreFunction) { . ([scriptblock]::Create($restoreFunction.Extent.Text)) }

function Stop-Everything {
    $script:events.Add('stop')
    if ($script:stopFails) { throw 'Runtime could not be stopped' }
}
function Start-Services { $script:events.Add('start-db'); $script:events.Add('start-api') }
function Restore-DesktopServiceSnapshots {
    param([object[]]$Snapshots)
    $script:events.Add('restore-services')
    $script:restoredSnapshots = $Snapshots
    if ($script:servicesRestoreFails) { throw 'Fixture service registration failure' }
}
$serviceSnapshots = @([pscustomobject]@{ Name = 'fake-db' }, [pscustomobject]@{ Name = 'fake-api' })
$script:servicesRestoreFails = $false
function Start-DatabaseForRestore { $script:events.Add('start-db') }
function Get-PostgresConnection {
    [pscustomobject]@{ DbHost = 'fake'; Port = '1'; Username = 'fake'; Database = 'fake'; Password = 'fixture-only' }
}
function Invoke-FakeRestore { $script:events.Add('restore'); $global:LASTEXITCODE = $script:restoreExit }
function Start-Sleep { param($Seconds, $Milliseconds) }
function Write-Step { param($Message) }
function Write-Warning { param($Message) }
function Write-Error { param($Message) $script:lastError = [string]$Message }
function Assert-True { param([bool]$Condition, [string]$Message) if (-not $Condition) { throw $Message } }

$components = @()
$swappedComponents = @()
$backupFile = 'fixture.dump'
$pgRestore = 'Invoke-FakeRestore'
$DbServiceName = 'fake-db'
$ApiServiceName = 'fake-api'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('nodepilot-desktop-rollback-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$DesktopJson = Join-Path $fixtureRoot 'desktop.json'
$previousDesktopJsonBytes = $null
$previousDesktopJsonAcl = $null
$componentAcls = @{}
$previousPassword = [Environment]::GetEnvironmentVariable('PGPASSWORD')
try {
    foreach ($exitCode in @(0, 1)) {
        $script:stopFails = $false
        $script:events = New-Object 'System.Collections.Generic.List[string]'
        $script:restoreExit = $exitCode
        $script:lastError = ''
        [Environment]::SetEnvironmentVariable('PGPASSWORD', 'prior-fixture-value')
        $_ = [pscustomobject]@{ Exception = [Exception]::new('Update health check failed') }
        & $rollback

        $restoreIndex = $script:events.IndexOf('restore')
        $apiIndex = $script:events.IndexOf('start-api')
        $servicesIndex = $script:events.IndexOf('restore-services')
        Assert-True ($restoreIndex -ge 0) 'Rollback must attempt the database restore.'
        Assert-True ($servicesIndex -ge 0 -and $servicesIndex -lt $restoreIndex -and
                     $servicesIndex -lt $script:events.IndexOf('start-db')) 'Rollback must restore both service registrations before the database restore starts PostgreSQL.'
        Assert-True (($script:restoredSnapshots | ForEach-Object { $_.Name }) -join ',' -eq 'fake-db,fake-api') 'Rollback must restore the snapshots taken before the update.'
        if ($exitCode -eq 0) {
            Assert-True ($apiIndex -gt $restoreIndex) 'API must remain stopped until the database restore succeeds.'
            Assert-True ($script:lastError -eq 'Update rolled back to the previous version.') 'Successful rollback should be identified accurately.'
        } else {
            Assert-True ($apiIndex -eq -1) 'A failed restore must not restart the API against a partial database.'
            Assert-True ($script:lastError -match 'rollback failed') 'Failed restore must be reported as failed rollback.'
        }
        Assert-True ([Environment]::GetEnvironmentVariable('PGPASSWORD') -eq 'prior-fixture-value') 'Restore must preserve the caller environment.'
    }
    $script:events.Clear()
    $script:stopFails = $true
    & $rollback
    Assert-True (-not $script:events.Contains('restore')) 'Failed shutdown must prevent database restore.'
    Assert-True (-not $script:events.Contains('start-api')) 'Failed shutdown must not restart the API.'

    # A service that cannot be registered again stops the rollback before PostgreSQL is touched.
    $script:events.Clear()
    $script:stopFails = $false
    $script:servicesRestoreFails = $true
    $script:lastError = ''
    & $rollback
    $script:servicesRestoreFails = $false
    Assert-True (-not $script:events.Contains('restore') -and -not $script:events.Contains('start-api')) 'A failed service restoration must prevent database restore and API start.'
    Assert-True ($script:lastError -match 'rollback failed') 'A failed service restoration must be reported as failed rollback.'

    # A changed free-port choice must roll back together with the old API configuration.
    $script:stopFails = $false
    $script:restoreExit = 0
    $InstallPath = Join-Path $fixtureRoot 'install'
    $rollbackRoot = Join-Path $fixtureRoot 'rollback'
    $newApp = Join-Path $InstallPath 'app'
    $savedApp = Join-Path $rollbackRoot 'app'
    New-Item -ItemType Directory -Path $newApp, $savedApp -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $newApp 'appsettings.Production.json'), '{"port":47000}')
    [IO.File]::WriteAllText((Join-Path $savedApp 'appsettings.Production.json'), '{"port":47001}')
    [IO.File]::WriteAllText($DesktopJson, '{"origin":"https://localhost:47001"}')
    $oldAcl = Get-Acl -LiteralPath $DesktopJson
    $oldAcl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $DesktopJson -AclObject $oldAcl
    $previousDesktopJsonBytes = [IO.File]::ReadAllBytes($DesktopJson)
    $previousDesktopJsonAcl = Get-Acl -LiteralPath $DesktopJson
    $oldAccess = $previousDesktopJsonAcl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    [IO.File]::WriteAllText($DesktopJson, '{"origin":"https://localhost:47000"}')
    $newAcl = Get-Acl -LiteralPath $DesktopJson
    $newAcl.SetAccessRuleProtection($false, $true)
    Set-Acl -LiteralPath $DesktopJson -AclObject $newAcl
    # The install directory grants Users read; the rollback copy lost it to the data directory's ACL.
    $usersSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $installAcl = Get-Acl -LiteralPath $InstallPath
    $installAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($usersSid, 'ReadAndExecute',
        'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $InstallPath -AclObject $installAcl
    $componentAcls = @{ app = (Get-Acl -LiteralPath $newApp) }
    $savedAcl = New-Object Security.AccessControl.DirectorySecurity
    $savedAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))) {
        $savedAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $savedApp -AclObject $savedAcl
    $swappedComponents = @('app')
    & $rollback
    foreach ($restoredPath in @($newApp, (Join-Path $newApp 'appsettings.Production.json'))) {
        $usersRead = @((Get-Acl -LiteralPath $restoredPath).Access | Where-Object {
            $_.IsInherited -and $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]) -eq $usersSid })
        Assert-True ($usersRead.Count -gt 0) "Rollback must give '$restoredPath' the install directory's inherited access back."
    }
    $componentAcls = @{}
    $restoredApp = Get-Content -LiteralPath (Join-Path $newApp 'appsettings.Production.json') -Raw | ConvertFrom-Json
    $restoredDesktop = Get-Content -LiteralPath $DesktopJson -Raw | ConvertFrom-Json
    Assert-True ($restoredDesktop.origin -eq "https://localhost:$($restoredApp.port)") 'Rollback must restore the desktop origin together with the old API port.'
    $restoredAccess = (Get-Acl -LiteralPath $DesktopJson).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    Assert-True ($restoredAccess -eq $oldAccess) 'Rollback must restore the original desktop configuration ACL.'

    $swappedComponents = @()
    $script:events.Clear()
    function Set-Acl { param($LiteralPath, $AclObject) throw 'Fixture desktop ACL restore failure' }
    try { & $rollback } finally { Remove-Item Function:\Set-Acl }
    Assert-True (-not $script:events.Contains('restore')) 'Failed desktop ACL restoration must prevent database restore.'
    Assert-True (-not $script:events.Contains('start-api')) 'Failed desktop ACL restoration must not restart the API.'
    Assert-True ($script:lastError -match 'rollback failed') 'Failed desktop ACL restoration must be reported as failed rollback.'

    $previousDesktopJsonBytes = $null
    $previousDesktopJsonAcl = $null
    & $rollback
    Assert-True (-not (Test-Path -LiteralPath $DesktopJson)) 'Rollback must remove a handoff created by a failed update when none existed before.'

    # Exercise the actual readiness loop independently; only its native calls are simulated.
    $readyFunction = $program.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Start-DatabaseForRestore'
    }, $false)
    . ([scriptblock]::Create($readyFunction.Extent.Text))
    function Start-Service { param($Name) $script:events.Add('start-db') }
    function Join-Path { param($Path, $ChildPath) return 'Invoke-FakeReady' }
    function Invoke-FakeReady {
        $script:readyCalls++
        $global:LASTEXITCODE = if ($script:readyCalls -ge $script:readyAfter) { 0 } else { 1 }
    }
    $pgBin = 'unused'
    $script:readyCalls = 0
    $script:readyAfter = 3
    Start-DatabaseForRestore -Connection (Get-PostgresConnection)
    Assert-True ($script:readyCalls -eq 3) 'Readiness must wait until PostgreSQL accepts connections.'
    $script:readyCalls = 0
    $script:readyAfter = 100
    $failure = $null
    try { Start-DatabaseForRestore -Connection (Get-PostgresConnection) } catch { $failure = $_ }
    Assert-True ($null -ne $failure -and $script:readyCalls -eq 30) 'Readiness must fail within its bounded attempt count.'
} finally {
    [Environment]::SetEnvironmentVariable('PGPASSWORD', $previousPassword)
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notlike 'nodepilot-desktop-rollback-*') {
        throw 'Refusing to remove an unexpected fixture path.'
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}

# The service snapshot helpers against a fixture key under HKCU: what the provisioner deletes is
# registered again with the same values, key ACL first, and a missing service fails explicitly.
& {
    # The readiness check above replaced Join-Path.
    Remove-Item -LiteralPath Function:\Join-Path -ErrorAction SilentlyContinue
    $runtimeAst = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'desktop\DesktopRuntime.ps1'), [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "DesktopRuntime.ps1 contains PowerShell syntax errors: $parseErrors" }
    foreach ($name in @('Get-DesktopServiceSnapshot', 'Restore-DesktopServiceSnapshots')) {
        $function = $runtimeAst.Find({ param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $false)
        if (-not $function) { throw "$name was not found in DesktopRuntime.ps1." }
        . ([scriptblock]::Create($function.Extent.Text))
    }
    $root = 'HKCU:\Software\NodePilotDesktopServiceFixture-' + [Guid]::NewGuid().ToString('N')
    $log = New-Object 'System.Collections.Generic.List[string]'
    function Remove-DesktopService { param($Name) $log.Add("remove:$Name"); Remove-Item -LiteralPath (Join-Path $root $Name) -Recurse -Force -ErrorAction SilentlyContinue }
    function Test-DesktopServiceExists { param($Name) Test-Path -LiteralPath (Join-Path $root $Name) }
    function Write-Host { param($Object) }
    function Invoke-CimMethod {
        param($ClassName, $MethodName, $Arguments)
        $log.Add("create:$($Arguments.Name)")
        $script:createArgs[$Arguments.Name] = $Arguments
        if ($script:createFails) { return [pscustomobject]@{ ReturnValue = 21 } }
        $key = New-Item -Path (Join-Path $root $Arguments.Name) -Force
        $key.SetValue('ImagePath', $Arguments.PathName, [Microsoft.Win32.RegistryValueKind]::ExpandString)
        return [pscustomobject]@{ ReturnValue = 0 }
    }
    $script:createArgs = @{}
    $script:createFails = $false
    try {
        New-Item -Path $root -Force | Out-Null
        $db = New-Item -Path (Join-Path $root 'fake-db') -Force
        $db.SetValue('ImagePath', '"C:\Program Files\NodePilot\pgsql\bin\pg_ctl.exe" runservice -N "fake-db" -o "-p 47100"', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        $db.SetValue('DisplayName', 'NodePilot Database')
        $db.SetValue('ObjectName', 'NT AUTHORITY\NetworkService')
        $db.SetValue('Start', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
        $db.SetValue('ErrorControl', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $db.SetValue('FailureActions', [byte[]](0x80, 0x51, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0x14, 0, 0, 0, 1, 0, 0, 0, 0x88, 0x13, 0, 0), [Microsoft.Win32.RegistryValueKind]::Binary)
        $api = New-Item -Path (Join-Path $root 'fake-api') -Force
        $api.SetValue('ImagePath', '"C:\Program Files\NodePilot\app\NodePilot.Api.exe" --contentRoot "C:\Program Files\NodePilot\app"', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        $api.SetValue('DisplayName', 'NodePilot')
        $api.SetValue('Description', 'NodePilot workflow orchestrator (desktop, loopback).')
        $api.SetValue('ObjectName', 'LocalSystem')
        $api.SetValue('Start', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
        $api.SetValue('ErrorControl', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $api.SetValue('DelayedAutostart', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $api.SetValue('DependOnService', [string[]]@('fake-db'), [Microsoft.Win32.RegistryValueKind]::MultiString)
        $api.SetValue('Environment', [string[]]@('ASPNETCORE_ENVIRONMENT=Production', 'ConnectionStrings__Postgres=Host=127.0.0.1;Port=47100;Password=fixture-only'), [Microsoft.Win32.RegistryValueKind]::MultiString)
        $apiAcl = Get-Acl -Path (Join-Path $root 'fake-api')
        $apiAcl.SetAccessRuleProtection($true, $true)
        Set-Acl -Path (Join-Path $root 'fake-api') -AclObject $apiAcl
        $apiAccess = (Get-Acl -Path (Join-Path $root 'fake-api')).GetSecurityDescriptorSddlForm('Access')

        $snapshots = @(
            Get-DesktopServiceSnapshot -Name 'fake-db' -ServicesRoot $root
            Get-DesktopServiceSnapshot -Name 'fake-api' -ServicesRoot $root
        )
        # What the provisioner does before it fails: both registrations are gone.
        Remove-Item -LiteralPath (Join-Path $root 'fake-db'), (Join-Path $root 'fake-api') -Recurse -Force
        $log.Clear()
        Restore-DesktopServiceSnapshots -Snapshots $snapshots -ServicesRoot $root
        Assert-True (($log -join ',') -eq 'remove:fake-api,remove:fake-db,create:fake-db,create:fake-api') 'Service restoration must remove dependents first and register the database before the API.'
        Assert-True ($script:createArgs['fake-db'].StartName -eq 'NT AUTHORITY\NetworkService' -and $script:createArgs['fake-db'].StartPassword -eq '') 'The database service must be registered again as NetworkService.'
        Assert-True ($script:createArgs['fake-api'].StartName -eq 'LocalSystem' -and -not $script:createArgs['fake-api'].ContainsKey('StartPassword')) 'The API service must be registered again as LocalSystem.'
        Assert-True ($script:createArgs['fake-api'].PathName -eq $snapshots[1].ImagePath -and @($script:createArgs['fake-api'].ServiceDependencies) -join ',' -eq 'fake-db') 'The API service keeps its quoted binary path and its dependency.'
        $restoredApi = Get-Item -LiteralPath (Join-Path $root 'fake-api')
        Assert-True ((@($restoredApi.GetValue('Environment')) -join "`n") -ceq ($snapshots[1].Environment -join "`n")) 'The API connection string must be restored exactly.'
        Assert-True ($restoredApi.GetValue('DelayedAutostart') -eq 1 -and $restoredApi.GetValue('Description') -eq $snapshots[1].Description) 'Delayed start and description must be restored.'
        Assert-True ((Get-Acl -Path (Join-Path $root 'fake-api')).GetSecurityDescriptorSddlForm('Access') -eq $apiAccess) 'The service key ACL must be restored.'
        $restoredFailure = [byte[]](Get-Item -LiteralPath (Join-Path $root 'fake-db')).GetValue('FailureActions')
        Assert-True (($restoredFailure -join ',') -eq ($snapshots[0].FailureActions -join ',')) 'Failure actions must be restored byte for byte.'

        $script:createFails = $true
        $failure = $null
        try { Restore-DesktopServiceSnapshots -Snapshots $snapshots -ServicesRoot $root } catch { $failure = $_ }
        $script:createFails = $false
        Assert-True ($null -ne $failure -and $failure.Exception.Message -match 'could not be registered again') 'A failed registration must fail the restoration explicitly.'

        $missing = $null
        try { [void](Get-DesktopServiceSnapshot -Name 'not-registered' -ServicesRoot $root) } catch { $missing = $_ }
        Assert-True ($null -ne $missing -and $missing.Exception.Message -match 'not registered') 'An update must refuse to start without both service registrations.'
    }
    finally {
        if ($root -like 'HKCU:\Software\NodePilotDesktopServiceFixture-*') {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# The mocks above set the exit code of a native command; do not leak it to the caller.
$global:LASTEXITCODE = 0
Write-Host 'Desktop rollback checks passed (database restore, service registrations, shutdown, desktop origin/ACL, readiness).'
