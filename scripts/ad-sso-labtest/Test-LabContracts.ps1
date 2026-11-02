# Synthetic regression tests. No AD commands, database, service, or HTTP transport runs.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/LabIdentity.ps1"
$checks = 0
function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
$identity = Resolve-NodePilotLabIdentity
Assert-Contract ($identity.Erin -eq 'nptest.erin' -and $identity.ServiceBindDn -eq 'CN=svc-nptest-dir,OU=NodePilot-SsoTest,DC=np,DC=lab') 'Default identities drifted.'
$custom = Resolve-NodePilotLabIdentity -UserPrefix 'lab2' -OuName 'Test' -DomainDn 'DC=example,DC=com'
Assert-Contract ($custom.Alice -eq 'lab2.alice' -and $custom.ServiceBindDn -eq 'CN=svc-lab2-dir,OU=Test,DC=example,DC=com') 'Custom identity was not propagated.'
foreach ($prefix in @("bad'prefix", 'bad_prefix', 'waytoolongprefix')) {
    $rejected = $false
    try { Resolve-NodePilotLabIdentity -UserPrefix $prefix | Out-Null } catch { $rejected = $true }
    Assert-Contract $rejected "Unsafe/invalid prefix accepted: $prefix"
}

function Read-ScriptAst([string]$Path) {
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    return $ast
}

# Run the real database evaluator in a child process; replace only its native query boundary.
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Assert-DbState.ps1'))
$ast = Read-ScriptAst (Join-Path $PSScriptRoot 'Assert-DbState.ps1')
$queryFunction = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-Psql' }, $true)
$queryFixture = @'
function Invoke-Psql([string]$sql, [switch]$Table) {
    if ($Table) { return }
    if ($sql -match 'IsTombstoned') { return '0' }
    if ($sql -notmatch "ILIKE '([^']+)'") { throw "Unscoped synthetic query: $sql" }
    $pattern = $Matches[1].Replace('%', '*')
    $actualPrefix = 'nptest'
    if ($env:LAB_FIXTURE_MODE -eq 'custom') { $actualPrefix = 'lab2' }
    if ($sql -match 'USER_DIRECTORY_ACCESS_REFUSED') { return [string][int]("$actualPrefix.bob" -like $pattern) }
    if ($sql -match 'USER_DIRECTORY_DEPROVISIONED') { return [string][int]("$actualPrefix.erin" -like $pattern) }
    if ($sql -match 'AuthSessions') {
        return [string][int]($env:LAB_FIXTURE_MODE -eq 'unsafe' -and "$actualPrefix.erin" -like $pattern)
    }
    if ($pattern -like '*bob*') {
        return [string][int]($env:LAB_FIXTURE_MODE -eq 'unsafe' -and "$actualPrefix.bob" -like $pattern)
    }
    return [string][int]("$actualPrefix.erin" -like $pattern)
}
'@
$safeSource = $source.Remove($queryFunction.Extent.StartOffset, $queryFunction.Extent.EndOffset - $queryFunction.Extent.StartOffset).Insert($queryFunction.Extent.StartOffset, $queryFixture)
$temp = Join-Path ([IO.Path]::GetTempPath()) ('NodePilotLabContracts-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
try {
    $fixturePath = Join-Path $temp 'Assert-DbState.ps1'
    [IO.File]::WriteAllText($fixturePath, $safeSource)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LabIdentity.ps1') -Destination $temp
    $shell = (Get-Process -Id $PID).Path
    foreach ($case in @(
        @{ Mode = 'unsafe'; Prefix = $null; Scenario = 'Gate'; Exit = 1 },
        @{ Mode = 'unsafe'; Prefix = $null; Scenario = 'Revocation'; Exit = 1 },
        @{ Mode = 'unsafe'; Prefix = 'nptest'; Scenario = 'Gate'; Exit = 1 },
        @{ Mode = 'unsafe'; Prefix = 'nptest'; Scenario = 'Revocation'; Exit = 1 },
        @{ Mode = 'safe'; Prefix = 'wrong'; Scenario = 'Gate'; Exit = 1 },
        @{ Mode = 'safe'; Prefix = 'wrong'; Scenario = 'Revocation'; Exit = 1 },
        @{ Mode = 'safe'; Prefix = 'nptest'; Scenario = 'Revocation'; Exit = 0 },
        @{ Mode = 'custom'; Prefix = 'lab2'; Scenario = 'Gate'; Exit = 0 },
        @{ Mode = 'custom'; Prefix = 'lab2'; Scenario = 'Revocation'; Exit = 0 }
    )) {
        $env:LAB_FIXTURE_MODE = $case.Mode
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $fixturePath,
            '-PsqlPath', $fixturePath, '-DbPassword', 'UNUSED_FIXTURE', '-Scenario', $case.Scenario)
        if ($case.Prefix) { $arguments += @('-UserPrefix', $case.Prefix) }
        $output = & $shell @arguments 2>&1
        Assert-Contract ($LASTEXITCODE -eq $case.Exit) "DB fixture $($case.Mode)/$($case.Prefix)/$($case.Scenario): $output"
    }
} finally {
    $env:LAB_FIXTURE_MODE = $null
    # The only deletion is this test's newly created, absolute temporary directory.
    if (([IO.Path]::GetFullPath($temp)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}

# Extract the actual optional drill, preserving all control flow and its result recording.
$ast = Read-ScriptAst (Join-Path $PSScriptRoot 'ad-sso-live-test.ps1')
$drillAst = $ast.Find({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$IncludeRevocationDrill' }, $true)
if (-not $drillAst) { throw 'Revocation drill not found.' }
$drill = [scriptblock]::Create($drillAst.Extent.Text)
Add-Type -TypeDefinition @'
public sealed class LabFixtureResponse { public int StatusCode { get; set; } }
public sealed class LabFixtureHttpException : System.Exception {
    public LabFixtureResponse Response { get; private set; }
    public LabFixtureHttpException(int status) : base("Synthetic HTTP failure") {
        Response = new LabFixtureResponse { StatusCode = status };
    }
}
'@
foreach ($case in @('401', '403', '500', 'network', 'admin-down', 'start-forbidden', 'missing-id', 'viewer', 'not-running')) {
    $script:case = $case
    $script:now = [DateTime]::UtcNow
    $script:removed = 0; $script:restored = 0
    $results = New-Object System.Collections.ArrayList
    $IncludeRevocationDrill = $true; $dbReady = $false
    $Base = 'https://fixture.invalid'; $UserPrefix = 'nptest'; $AccessGroup = 'NPTest-Access'; $DcFqdn = 'fixture.invalid'
    $adminSession = 'admin'; $RevocationWorkflowId = 'b957f91a-312b-4dc9-ad10-9254df36b834'; $RevocationTimeoutMinutes = 16
    function Add-Result($Name, $Ok, $Detail) { [void]$results.Add(@{ Name = $Name; Ok = $Ok; Detail = $Detail }) }
    function Add-Skip($Name, $Detail) { [void]$results.Add(@{ Name = $Name; Ok = $null; Detail = $Detail }) }
    function Import-Module { param($Name, $ErrorAction) }
    function New-LabCredential($Sam) { return $Sam }
    function Invoke-SsoLogin($Credential) { return @{ Status = 200; Session = 'erin'; Body = @{ role = $(if ($script:case -eq 'viewer') { 'Viewer' } else { 'Operator' }) } } }
    function Get-SessionCookie($Session, $Name) { return @{ Value = 'fixture' } }
    function Invoke-JsonPost($Url, $Body, $Headers, $Session) {
        return @{ Status = $(if ($script:case -eq 'start-forbidden') { 403 } else { 202 }); Body = @{ id = $(if ($script:case -eq 'missing-id') { $null } else { $RevocationWorkflowId }) } }
    }
    function Remove-ADGroupMember { param($Identity, $Members, $Server, $Confirm) $script:removed++ }
    function Add-ADGroupMember { param($Identity, $Members, $Server) $script:restored++ }
    function Start-Sleep { param($Seconds) $script:now = $script:now.AddSeconds($Seconds) }
    function Get-Date { return $script:now }
    function Invoke-RestMethod {
        param($Uri, $WebSession)
        if ($Uri -like '*/executions/*') {
            return @{ status = $(if ($script:case -eq 'not-running') { 'Succeeded' } elseif ($script:removed) { 'Cancelled' } else { 'Running' }) }
        }
        if ($WebSession -eq 'admin') {
            if ($script:case -eq 'admin-down') { throw [LabFixtureHttpException]::new(503) }
            return @{ role = 'Admin' }
        }
        if ($script:case -eq 'network') { throw 'Synthetic network timeout' }
        $status = 401
        if ($script:case -in @('403', '500')) { $status = [int]$script:case }
        throw [LabFixtureHttpException]::new($status)
    }
    & $drill | Out-Null
    $passes = @($results | Where-Object { $_.Ok -eq $true })
    $failures = @($results | Where-Object { $_.Ok -eq $false })
    if ($case -in @('401', '403')) {
        Assert-Contract ($passes.Count -eq 2 -and $failures.Count -eq 0) "Valid revocation rejected ($case): $($results | ConvertTo-Json -Compress)"
    } else {
        Assert-Contract ($passes.Count -eq 0 -and $failures.Count -eq 1) "Failure became successful revocation ($case): $($results | ConvertTo-Json -Compress)"
    }
    Assert-Contract ($script:removed -eq $script:restored) "Membership not restored in $case."
    if ($case -in @('start-forbidden', 'missing-id', 'viewer', 'not-running')) {
        Assert-Contract ($script:removed -eq 0) "Membership changed despite failed precondition in $case."
    }
}
Write-Host "Lab contract tests passed ($checks assertions)."
