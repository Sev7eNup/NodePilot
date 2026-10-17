#requires -Version 5.1

Set-StrictMode -Version 3.0

function Send-NodePilotProgress {
    [CmdletBinding()]
    param(
        [scriptblock]$Callback,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('working', 'failed', 'complete', 'rollback')][string]$State = 'working',
        [long]$Current = 0,
        [long]$Total = 0,
        [string]$Detail = ''
    )
    if (-not $Callback) { return }
    try {
        $null = & $Callback ([pscustomobject]@{
            Phase = $Phase; Message = $Message; State = $State
            Current = $Current; Total = $Total; Detail = $Detail
        })
    } catch { Write-Verbose "Progress could not be reported: $($_.Exception.Message)" }
}

function Write-NodePilotProgressFile {
    [CmdletBinding()]
    param([string]$Path, [Parameter(Mandatory)]$Progress)
    if (-not $Path) { return }
    # One record per line; the reader ignores an incomplete final record.
    $position = if ($Progress.Total -gt 0) { [int][Math]::Min(100, [Math]::Floor(100.0 * $Progress.Current / $Progress.Total)) } else { 0 }
    $fields = @($Progress.State, $Progress.Phase, $position, ([int]($Progress.Total -gt 0)),
        $Progress.Message, $Progress.Detail) | ForEach-Object { ([string]$_) -replace '[|\r\n]', ' ' }
    [IO.File]::AppendAllText($Path, (($fields -join '|') + "|end`r`n"), [Text.Encoding]::UTF8)
}

function Copy-NodePilotDirectoryWithProgress {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [scriptblock]$ProgressCallback
    )
    $sourceRoot = (Get-Item -LiteralPath $Source -ErrorAction Stop).FullName.TrimEnd('\') + '\'
    $targetRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    if ($targetRoot.StartsWith($sourceRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The copy destination must not be inside the source directory.'
    }
    $entries = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force -ErrorAction Stop)
    if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'The copy source must not contain reparse points.'
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer })
    [long]$total = 0
    foreach ($file in $files) { $total += $file.Length }
    [long]$copied = 0
    $completed = 0
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $buffer = New-Object byte[] (1024 * 1024)
    [void][IO.Directory]::CreateDirectory($targetRoot)
    foreach ($directory in @($entries | Where-Object { $_.PSIsContainer })) {
        [void][IO.Directory]::CreateDirectory((Join-Path $targetRoot $directory.FullName.Substring($sourceRoot.Length)))
    }
    Send-NodePilotProgress -Callback $ProgressCallback -Phase 'copy' -Message 'Copying program files' -Total $total
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($sourceRoot.Length)
        $target = Join-Path $targetRoot $relative
        $inputStream = [IO.File]::OpenRead($file.FullName)
        try {
            $outputStream = [IO.File]::Open($target, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $outputStream.Write($buffer, 0, $read)
                    $copied += $read
                    if ($timer.ElapsedMilliseconds -ge 250) {
                        $detail = '{0:N1} / {1:N1} MB; {2} / {3} files completed - {4}' -f ($copied / 1MB), ($total / 1MB), $completed, $files.Count, $relative
                        Send-NodePilotProgress -Callback $ProgressCallback -Phase 'copy' -Message 'Copying program files' -Current $copied -Total $total -Detail $detail
                        $timer.Restart()
                    }
                }
            } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
        [IO.File]::SetLastWriteTimeUtc($target, $file.LastWriteTimeUtc)
        [IO.File]::SetAttributes($target, $file.Attributes)
        $completed++
    }
    Send-NodePilotProgress -Callback $ProgressCallback -Phase 'copy' -Message 'Program files copied' -Current $copied -Total $total -Detail "$completed / $($files.Count) files completed"
}
