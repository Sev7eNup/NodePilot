<#
.SYNOPSIS
  NodePilot nightly test run: backend (dotnet test), frontend units (vitest), E2E (Playwright).

.DESCRIPTION
  Runs all three automated suites against the *currently checked-out* working tree and writes
  a timestamped report plus per-suite logs. Exits non-zero if any suite fails, so the Windows
  Task Scheduler "Last Run Result" reflects the outcome.

  All three suites are HERMETIC — they need no running NodePilot stack:
    - dotnet test       : in-memory SQLite + mocked WinRM
    - npm run test:run  : vitest under jsdom
    - npm run test:e2e  : builds the SPA, serves it via `vite preview`, mocks every /api call
                          with page.route(); no backend, no Postgres.
  So this script does NOT start Postgres / the API / the dev server.

  It does NOT run `git pull` — it tests whatever is checked out, which is what you want for
  catching regressions in work-in-progress. Pull first yourself if you want to test origin/main.

.PARAMETER RepoRoot
  Repository root. Defaults to E:\NodePilot.

.PARAMETER OutputDir
  Where run folders are written. Defaults to C:\temp\nodepilot-nightly.

.PARAMETER KeepRuns
  How many past run folders to retain (older ones are pruned). Default 14.
#>
[CmdletBinding()]
param(
  [string]$RepoRoot  = 'E:\NodePilot',
  [string]$OutputDir = 'C:\temp\nodepilot-nightly',
  [int]   $KeepRuns  = 14
)

$ErrorActionPreference = 'Continue'
$uiDir = Join-Path $RepoRoot 'src\nodepilot-ui'
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$runDir = Join-Path $OutputDir $stamp
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

function Get-Count([string]$text, [string]$pattern) {
  $m = [regex]::Match($text, $pattern)
  if ($m.Success) { return [int]$m.Groups[1].Value } else { return $null }
}

# Runs one suite, tees output to a log, captures the native exit code, returns a result object.
# On failure it retries once (Retries=1): these suites contain a few timing-sensitive
# concurrency tests that can flake on a loaded host. A genuine failure fails both attempts;
# a flake passes on retry. The result is flagged Retried=$true when a second attempt ran.
function Invoke-Suite {
  param([string]$Name, [string]$WorkDir, [scriptblock]$Action, [int]$Retries = 1)
  $start = Get-Date
  $code = 1
  $retried = $false
  for ($attempt = 0; $attempt -le $Retries; $attempt++) {
    $log = Join-Path $runDir ($(if ($attempt -eq 0) { "$Name.log" } else { "$Name.retry$attempt.log" }))
    if ($attempt -gt 0) { $retried = $true; Write-Host "  $Name failed (exit $code) - retry $attempt/$Retries" }
    Push-Location $WorkDir
    try {
      & $Action *>&1 | Tee-Object -FilePath $log | Out-Null
      $code = $LASTEXITCODE
    } catch {
      $_ | Out-File -FilePath $log -Append -Encoding utf8
      $code = 1
    } finally {
      Pop-Location
    }
    if ($code -eq 0) { break }
  }
  $dur = [int]((Get-Date) - $start).TotalSeconds
  [pscustomobject]@{
    Suite    = $Name
    ExitCode = $code
    Pass     = ($code -eq 0)
    Retried  = $retried
    Seconds  = $dur
    Log      = $log
    LogText  = (Get-Content $log -Raw -ErrorAction SilentlyContinue)
  }
}

Write-Host "NodePilot nightly run $stamp -> $runDir"
$results = @()

# `dotnet test` rebuilds the whole solution; that build fails with MSB3027 ("being used by
# another process" → test projects silently don't run) whenever a build output is locked by:
#   (a) a running NodePilot.Api dev host on port 5000  -> locks Engine/Scheduler *.pdb
#   (b) an orphaned testhost/vstest.console from a prior `dotnet test` -> locks np.dll etc.
# Clear both before each backend attempt. Safe for a test run: the suites are hermetic and
# don't need the API (restart it afterwards with `dotnet run` if you had one up), and
# testhost/vstest.console are throwaway test runners.
function Clear-BuildLocks {
  Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { Write-Host "  freeing port 5000 (pid $_)"; Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
  $stale = Get-Process -Name testhost, vstest.console -ErrorAction SilentlyContinue
  if ($stale) { Write-Host "  killing $($stale.Count) stale testhost/vstest process(es)"; $stale | Stop-Process -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 700
}
Clear-BuildLocks

# 1) Backend — dotnet test (whole solution). Re-clear locks at the start of each attempt so
# the retry isn't sabotaged by the first attempt's own lingering testhost.
$results += Invoke-Suite -Name 'backend-dotnet' -WorkDir $RepoRoot -Action {
  Clear-BuildLocks
  dotnet test --nologo
}

# 2) Frontend unit tests — vitest
$results += Invoke-Suite -Name 'frontend-vitest' -WorkDir $uiDir -Action {
  npm run test:run
}

# 3) E2E — Playwright. CI=true forces a fresh build + vite preview (reuseExistingServer off);
#    --reporter=line avoids the HTML/GitHub reporters (the HTML one trips EPERM writing
#    playwright-report\ on this box, and the GitHub reporter is for Actions only).
#    Free port 4173 first in case a previous preview lingered.
$results += Invoke-Suite -Name 'frontend-e2e' -WorkDir $uiDir -Action {
  Get-NetTCPConnection -LocalPort 4173 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
  $env:CI = 'true'
  npx playwright test --reporter=line
  $env:CI = $null
}

# --- Parse best-effort counts for the report ---
foreach ($r in $results) {
  $t = [string]$r.LogText
  switch ($r.Suite) {
    'backend-dotnet'  {
      $passed = ([regex]::Matches($t, 'erfolgreich:\s*(\d+)') + [regex]::Matches($t, 'Passed:\s*(\d+)') |
                 ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
      $failed = ([regex]::Matches($t, 'Fehler:\s*(\d+)')     + [regex]::Matches($t, 'Failed:\s*(\d+)') |
                 ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
      $r | Add-Member Detail "passed=$passed failed=$failed" -PassThru | Out-Null
    }
    'frontend-vitest' {
      $r | Add-Member Detail ("Tests " + (Get-Count $t 'Tests\s+(\d+)\s+passed') + " passed") -PassThru | Out-Null
    }
    'frontend-e2e'    {
      $r | Add-Member Detail ((Get-Count $t '(\d+)\s+passed').ToString() + " passed") -PassThru | Out-Null
    }
  }
}

# --- Write the report ---
$anyFail = ($results | Where-Object { -not $_.Pass } | Measure-Object).Count -gt 0
$overall = if ($anyFail) { 'FAIL' } else { 'PASS' }
$lines = @()
$lines += "# NodePilot Nightly Test Report"
$lines += ""
$lines += "- Run: $stamp"
$lines += "- Overall: **$overall**"
$lines += ""
$lines += "| Suite | Result | Detail | Duration |"
$lines += "|---|---|---|---|"
foreach ($r in $results) {
  $res = if (-not $r.Pass) { "FAIL (exit $($r.ExitCode), after retry)" } elseif ($r.Retried) { 'PASS (flaky - retried)' } else { 'PASS' }
  $lines += "| $($r.Suite) | $res | $($r.Detail) | $($r.Seconds)s |"
}
$lines += ""
$lines += "Per-suite logs: ``$runDir``"
$report = $lines -join "`r`n"
$report | Out-File -FilePath (Join-Path $runDir 'report.md') -Encoding utf8
$report | Out-File -FilePath (Join-Path $OutputDir 'latest.md') -Encoding utf8
Write-Host $report

# --- Retention: keep the newest $KeepRuns run folders ---
Get-ChildItem $OutputDir -Directory -ErrorAction SilentlyContinue |
  Sort-Object Name -Descending | Select-Object -Skip $KeepRuns |
  ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

if ($anyFail) { exit 1 } else { exit 0 }
