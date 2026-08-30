"""Filesystem and script coverage: runScript, fileOperation, folderOperation, fileHash,
zipOperation, textFileEdit.

Every one of these runs against localhost (in-process bypass) inside runs/<cid>, and every
one cleans up before its assertion so a red assertion cannot leave residue behind.
"""

from suitelib import Step, Workflow, RUNS_ROOT
from spec_core import (LOCAL, janitor, cid, mkrun, cleanup, assert_step, ok_return,
                       CID, RUN_DIR)

SEED_HEAD = "$cid = " + CID + "\n$dir = Join-Path '" + RUNS_ROOT + "' $cid\n"


def run_script_workflow():
    steps = [
        janitor(), cid(), mkrun(),
        Step("v0", "runScript: structured output", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": "$hostName = $env:COMPUTERNAME\n$answer = '42'\n"
                        "Write-Output 'captured'\n"},
             target_machine=LOCAL,
             cases=[{"id": "runScript.output.params", "dimension": "runScript.output",
                     "value": "declared variables captured as param.*"}]),
        Step("v1", "runScript: engine runspace", "runScript",
             {"engine": "runspace", "timeoutSeconds": 30,
              "script": "$engineUsed = 'runspace'\nWrite-Output 'runspace ok'\n"},
             target_machine=LOCAL,
             cases=[{"id": "runScript.engine.runspace", "dimension": "runScript.engine",
                     "value": "runspace"}]),
        Step("v2", "runScript: engine powershell (5.1)", "runScript",
             {"engine": "powershell", "timeoutSeconds": 60,
              "script": "$psMajor = $PSVersionTable.PSVersion.Major.ToString()\n"
                        "Write-Output 'windows powershell ok'\n"},
             target_machine=LOCAL,
             cases=[{"id": "runScript.engine.powershell",
                     "dimension": "runScript.engine", "value": "powershell (5.1)"}]),
        Step("v3", "runScript: isolated + caps", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "isolated": True,
              "memoryLimitMb": 512, "maxProcesses": 8,
              "script": "$isolatedPid = $PID.ToString()\nWrite-Output 'isolated ok'\n"},
             target_machine=LOCAL,
             cases=[{"id": "runScript.isolated.true", "dimension": "runScript.isolated",
                     "value": "true"},
                    {"id": "runScript.memoryLimitMb", "dimension": "runScript.memoryLimitMb",
                     "value": "512 (isolated only)"},
                    {"id": "runScript.maxProcesses", "dimension": "runScript.maxProcesses",
                     "value": "8 (isolated only)"}]),
        Step("v4", "runScript: successExitCodes 0,3", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "successExitCodes": "0,3",
              "script": "cmd /c exit 3\nWrite-Output 'native exit captured'\n"},
             target_machine=LOCAL,
             cases=[{"id": "runScript.successExitCodes.non-zero-ok",
                     "dimension": "runScript.successExitCodes", "value": "0,3"}]),
        Step("v5", "runScript: transcript", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "transcript": True,
              "script": "Write-Output 'transcript line'\n$transcriptProbe = 'yes'\n"},
             target_machine=LOCAL,
             cases=[{"id": "runScript.transcript.true", "dimension": "runScript.transcript",
                     "value": "true"}]),
        Step("v6", "runScript: retry fixed", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "retry": {"maxAttempts": 3, "backoff": "fixed", "initialDelayMs": 200,
                        "maxDelayMs": 1000},
              "script": "$retried = 'ok'\nWrite-Output 'retry block accepted'\n"},
             target_machine=LOCAL,
             cases=[{"id": "retry.backoff.fixed", "dimension": "retry.backoff",
                     "value": "fixed"}]),
        Step("v7", "runScript: retry linear", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "retry": {"maxAttempts": 2, "backoff": "linear", "initialDelayMs": 200},
              "script": "Write-Output 'linear'\n"},
             target_machine=LOCAL,
             cases=[{"id": "retry.backoff.linear", "dimension": "retry.backoff",
                     "value": "linear"}]),
        Step("v8", "runScript: retry exponential", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "retry": {"maxAttempts": 2, "backoff": "exponential", "initialDelayMs": 200,
                        "maxDelayMs": 2000},
              "script": "Write-Output 'exponential'\n"},
             target_machine=LOCAL,
             cases=[{"id": "retry.backoff.exponential", "dimension": "retry.backoff",
                     "value": "exponential"}]),
        cleanup(),
        assert_step("""
$hostName = {{v0.param.hostName}}
$answer   = {{v0.param.answer}}
$runspace = {{v1.param.engineUsed}}
$psMajor  = {{v2.param.psMajor}}
$isoOut   = {{v3.output}}
$exit3    = {{v4.param.exitCode}}
$transcr  = {{v5.output}}
$retried  = {{v6.param.retried}}
$linear   = {{v7.output}}
$expo     = {{v8.output}}

if ([string]::IsNullOrWhiteSpace($hostName)) { throw "runScript did not capture \\$hostName" }
if ($answer -ne '42') { throw "runScript param capture: expected 42, got '$answer'" }
if ($runspace -ne 'runspace') { throw "runspace engine capture: '$runspace'" }
if ($psMajor -ne '5') { throw "engine=powershell should be Windows PowerShell 5.1, got major '$psMajor'" }
if ($isoOut -notmatch 'isolated ok') { throw "isolated run produced: '$isoOut'" }
if ($exit3 -ne '3') { throw "successExitCodes: expected exitCode 3, got '$exit3'" }
if ($transcr -notmatch 'transcript line') { throw "transcript output: '$transcr'" }
if ($retried -ne 'ok') { throw "retry block changed the result: '$retried'" }
if ($linear -notmatch 'linear') { throw "linear backoff variant produced: '$linear'" }
if ($expo -notmatch 'exponential') { throw "exponential backoff variant produced: '$expo'" }
$assertOk = 'runScript'
"""),
        ok_return("runScript", with_cid=True),
    ]
    return Workflow(
        15, "runScript", "[TestSuite] runScript",
        "Structured parameter capture, the runspace and Windows PowerShell engines, "
        "process isolation with its caps, native exit-code acceptance, transcript "
        "capture and all three retry backoffs.",
        "positive", "continuous", "A", steps, max_runtime=120,
        excluded=[
            {"id": "runScript.engine.pwsh", "dimension": "runScript.engine", "value": "pwsh",
             "reason": "Spawns an external PowerShell 7. A host without pwsh.exe would "
                       "carry a permanently red step, so the engine choice is asserted in "
                       "unit tests rather than in the cadence.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/RunScriptExecutionTargetTests.cs"},
        ])


def file_operation_workflow():
    a = RUN_DIR + r"\a.txt"
    b = RUN_DIR + r"\b.txt"
    c = RUN_DIR + r"\c.txt"
    d = RUN_DIR + r"\d.txt"
    steps = [
        janitor(), cid(), mkrun(),
        Step("v0", "fileOp: create", "fileOperation",
             {"operation": "create", "path": a}, target_machine=LOCAL,
             cases=[{"id": "fileOperation.operation.create", "assertedVia": "v1",
                     "dimension": "fileOperation.operation", "value": "create"}]),
        Step("v1", "fileOp: exists (true)", "fileOperation",
             {"operation": "exists", "path": a}, target_machine=LOCAL,
             cases=[{"id": "fileOperation.operation.exists-true",
                     "dimension": "fileOperation.operation", "value": "exists (present)"}]),
        Step("v2", "fileOp: copy", "fileOperation",
             {"operation": "copy", "path": a, "destination": b}, target_machine=LOCAL,
             cases=[{"id": "fileOperation.operation.copy",
                     "dimension": "fileOperation.operation", "value": "copy"}]),
        Step("v3", "fileOp: rename", "fileOperation",
             {"operation": "rename", "path": b, "newName": "c.txt"}, target_machine=LOCAL,
             cases=[{"id": "fileOperation.operation.rename",
                     "dimension": "fileOperation.operation", "value": "rename"}]),
        Step("v4", "fileOp: move", "fileOperation",
             {"operation": "move", "path": c, "destination": d}, target_machine=LOCAL,
             cases=[{"id": "fileOperation.operation.move", "assertedVia": "v5",
                     "dimension": "fileOperation.operation", "value": "move"}]),
        Step("v5", "fileOp: exists (false)", "fileOperation",
             {"operation": "exists", "path": c}, target_machine=LOCAL,
             cases=[{"id": "fileOperation.operation.exists-false",
                     "dimension": "fileOperation.operation", "value": "exists (absent)"}]),
        Step("v6", "fileOp: delete", "fileOperation",
             {"operation": "delete", "path": d}, target_machine=LOCAL,
             cases=[{"id": "fileOperation.operation.delete", "assertedVia": "v7",
                     "dimension": "fileOperation.operation", "value": "delete"}]),
        Step("v7", "fileOp: exists after delete", "fileOperation",
             {"operation": "exists", "path": d}, target_machine=LOCAL),
        cleanup(),
        assert_step("""
$existsAfterCreate = {{v1.param.exists}}
$existsAfterMove   = {{v5.param.exists}}
$existsAfterDelete = {{v7.param.exists}}
$copyDest = {{v2.param.destination}}
$renamed  = {{v3.param.newPath}}

if ($existsAfterCreate -ne 'True' -and $existsAfterCreate -ne 'true') {
  throw "fileOperation exists after create: '$existsAfterCreate'"
}
if ($existsAfterMove -eq 'True' -or $existsAfterMove -eq 'true') {
  throw "fileOperation: the source should be gone after a move"
}
if ($existsAfterDelete -eq 'True' -or $existsAfterDelete -eq 'true') {
  throw "fileOperation: the file should be gone after delete"
}
if ($copyDest -notmatch 'b.txt') { throw "fileOperation copy destination: '$copyDest'" }
if ($renamed -notmatch 'c.txt')  { throw "fileOperation rename newPath: '$renamed'" }
$assertOk = 'fileOperation'
"""),
        ok_return("fileOperation", with_cid=True),
    ]
    return Workflow(
        16, "fileOperation", "[TestSuite] fileOperation",
        "All six operations in one lifecycle, including both outcomes of exists.",
        "positive", "continuous", "A", steps, max_runtime=90)


def folder_operation_workflow():
    base = RUN_DIR + r"\fold"
    nested = RUN_DIR + r"\fold\deep\deeper"
    copied = RUN_DIR + r"\fold-copy"
    renamed = RUN_DIR + r"\fold-ren"
    moved = RUN_DIR + r"\fold-moved"
    steps = [
        janitor(), cid(), mkrun(),
        Step("v0", "folderOp: create", "folderOperation",
             {"operation": "create", "path": base}, target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.create", "assertedVia": "v2",
                     "dimension": "folderOperation.operation", "value": "create"}]),
        Step("v1", "folderOp: create nested", "folderOperation",
             {"operation": "create", "path": nested}, target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.create-nested", "assertedVia": "v3",
                     "dimension": "folderOperation.operation",
                     "value": "create (nested path)"}]),
        Step("v2", "folderOp: exists (true)", "folderOperation",
             {"operation": "exists", "path": base}, target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.exists-true",
                     "dimension": "folderOperation.operation",
                     "value": "exists (present)"}]),
        Step("v3", "folderOp: list", "folderOperation",
             {"operation": "list", "path": base}, target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.list",
                     "dimension": "folderOperation.operation", "value": "list"}]),
        Step("v4", "folderOp: copy", "folderOperation",
             {"operation": "copy", "path": base, "destination": copied},
             target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.copy", "assertedVia": "v7",
                     "dimension": "folderOperation.operation", "value": "copy"}]),
        Step("v5", "folderOp: rename", "folderOperation",
             {"operation": "rename", "path": copied, "newName": "fold-ren"},
             target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.rename", "assertedVia": "v7",
                     "dimension": "folderOperation.operation", "value": "rename"}]),
        Step("v6", "folderOp: move", "folderOperation",
             {"operation": "move", "path": renamed, "destination": moved},
             target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.move", "assertedVia": "v7",
                     "dimension": "folderOperation.operation", "value": "move"}]),
        Step("v7", "folderOp: exists (false)", "folderOperation",
             {"operation": "exists", "path": renamed}, target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.exists-false",
                     "dimension": "folderOperation.operation",
                     "value": "exists (absent)"}]),
        Step("v8", "folderOp: delete", "folderOperation",
             {"operation": "delete", "path": moved}, target_machine=LOCAL,
             cases=[{"id": "folderOperation.operation.delete", "assertedVia": "v9",
                     "dimension": "folderOperation.operation", "value": "delete"}]),
        Step("v9", "folderOp: exists after delete", "folderOperation",
             {"operation": "exists", "path": moved}, target_machine=LOCAL),
        cleanup(),
        assert_step("""
$existsBase   = {{v2.param.exists}}
$listCount    = {{v3.param.count}}
$listItems    = {{v3.param.items}}
$existsMoved  = {{v7.param.exists}}
$goneAfterDel = {{v9.param.exists}}

if ($existsBase -ne 'True' -and $existsBase -ne 'true') {
  throw "folderOperation exists after create: '$existsBase'"
}
if ([int]$listCount -lt 1) { throw "folderOperation list count: expected the nested child, got '$listCount'" }
if ($listItems -notmatch 'deep') { throw "folderOperation list items: '$listItems'" }
if ($existsMoved -eq 'True' -or $existsMoved -eq 'true') {
  throw "folderOperation: the source should be gone after a move"
}
if ($goneAfterDel -eq 'True' -or $goneAfterDel -eq 'true') {
  throw "folderOperation: the folder should be gone after delete"
}
$assertOk = 'folderOperation'
"""),
        ok_return("folderOperation", with_cid=True),
    ]
    return Workflow(
        17, "folderOperation", "[TestSuite] folderOperation",
        "All seven operations including the nested create, both outcomes of exists and "
        "the list projection.",
        "positive", "continuous", "A", steps, max_runtime=90,
        excluded=[
            {"id": "folderOperation.list.truncation",
             "dimension": "folderOperation.operation", "value": "list over 5000 entries",
             "reason": "Creating 5001 files every five minutes to observe a truncation "
                       "flag is not a proportionate cadence cost.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/FolderOperationActivityTests.cs"},
        ])


HASH_TARGET = r"C:\Windows\System32\notepad.exe"


def file_hash_workflow():
    steps = [
        janitor(), cid(), mkrun(),
        Step("seed", "Seed: known content", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": SEED_HEAD +
                        "$path = Join-Path $dir 'hash.txt'\n"
                        "[IO.File]::WriteAllText($path, 'nodepilot')\n"
                        "$knownSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash\n"},
             target_machine=LOCAL),
        Step("v0", "hash: SHA256", "fileHash",
             {"path": RUN_DIR + r"\hash.txt", "algorithm": "SHA256"},
             target_machine=LOCAL,
             cases=[{"id": "fileHash.algorithm.SHA256",
                     "dimension": "fileHash.algorithm", "value": "SHA256"}]),
        Step("v1", "hash: SHA1", "fileHash",
             {"path": RUN_DIR + r"\hash.txt", "algorithm": "SHA1"}, target_machine=LOCAL,
             cases=[{"id": "fileHash.algorithm.SHA1",
                     "dimension": "fileHash.algorithm", "value": "SHA1"}]),
        Step("v2", "hash: MD5", "fileHash",
             {"path": RUN_DIR + r"\hash.txt", "algorithm": "MD5"}, target_machine=LOCAL,
             cases=[{"id": "fileHash.algorithm.MD5",
                     "dimension": "fileHash.algorithm", "value": "MD5"}]),
        Step("v3", "hash: SHA384", "fileHash",
             {"path": RUN_DIR + r"\hash.txt", "algorithm": "SHA384"}, target_machine=LOCAL,
             cases=[{"id": "fileHash.algorithm.SHA384",
                     "dimension": "fileHash.algorithm", "value": "SHA384"}]),
        Step("v4", "hash: SHA512", "fileHash",
             {"path": RUN_DIR + r"\hash.txt", "algorithm": "SHA512"}, target_machine=LOCAL,
             cases=[{"id": "fileHash.algorithm.SHA512",
                     "dimension": "fileHash.algorithm", "value": "SHA512"}]),
        Step("v5", "hash: default algorithm", "fileHash",
             {"path": RUN_DIR + r"\hash.txt"}, target_machine=LOCAL,
             cases=[{"id": "fileHash.algorithm.default",
                     "dimension": "fileHash.algorithm", "value": "(absent) -> SHA256"}]),
        Step("v6", "hash: expected matches", "fileHash",
             {"path": RUN_DIR + r"\hash.txt", "algorithm": "SHA256",
              "expected": "{{seed.param.knownSha256}}"}, target_machine=LOCAL,
             cases=[{"id": "fileHash.expected.match",
                     "dimension": "fileHash.expected", "value": "matching hash"}]),
        cleanup(),
        assert_step("""
$sha256 = {{v0.param.hash}}
$sha1   = {{v1.param.hash}}
$md5    = {{v2.param.hash}}
$sha384 = {{v3.param.hash}}
$sha512 = {{v4.param.hash}}
$dflt   = {{v5.param.hash}}
$dfltAlg = {{v5.param.algorithm}}
$match  = {{v6.param.match}}

if ($sha256.Length -ne 64) { throw "SHA256 length: $($sha256.Length)" }
if ($sha1.Length   -ne 40) { throw "SHA1 length: $($sha1.Length)" }
if ($md5.Length    -ne 32) { throw "MD5 length: $($md5.Length)" }
if ($sha384.Length -ne 96) { throw "SHA384 length: $($sha384.Length)" }
if ($sha512.Length -ne 128) { throw "SHA512 length: $($sha512.Length)" }
if ($dflt -ne $sha256) { throw "the default algorithm should be SHA256" }
if ($dfltAlg -ne 'SHA256') { throw "default algorithm reported as '$dfltAlg'" }
if ($match -ne 'True' -and $match -ne 'true') { throw "fileHash expected-match flag: '$match'" }
$assertOk = 'fileHash'
"""),
        ok_return("fileHash", with_cid=True),
    ]
    return Workflow(
        18, "fileHash", "[TestSuite] fileHash",
        "All five algorithms, the absent-algorithm default and a matching expected hash.",
        "positive", "continuous", "A", steps, max_runtime=60)


def zip_operation_workflow():
    steps = [
        janitor(), cid(), mkrun(),
        Step("seed", "Seed: files to archive", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": SEED_HEAD +
                        "$src = Join-Path $dir 'src'\n"
                        "New-Item -ItemType Directory -Path $src -Force | Out-Null\n"
                        "1..3 | ForEach-Object { Set-Content -LiteralPath (Join-Path $src \"f$_.txt\") -Value ('content ' * 200) }\n"
                        "$seeded = 'ok'\n"},
             target_machine=LOCAL),
        Step("v0", "zip: compress Optimal", "zipOperation",
             {"operation": "compress", "source": RUN_DIR + r"\src",
              "destination": RUN_DIR + r"\opt.zip",
              "compressionLevel": "Optimal", "force": True}, target_machine=LOCAL,
             cases=[{"id": "zipOperation.operation.compress",
                     "dimension": "zipOperation.operation", "value": "compress"},
                    {"id": "zipOperation.compressionLevel.Optimal",
                     "dimension": "zipOperation.compressionLevel", "value": "Optimal"}]),
        Step("v1", "zip: compress Fastest", "zipOperation",
             {"operation": "compress", "source": RUN_DIR + r"\src",
              "destination": RUN_DIR + r"\fast.zip",
              "compressionLevel": "Fastest", "force": True}, target_machine=LOCAL,
             cases=[{"id": "zipOperation.compressionLevel.Fastest",
                     "dimension": "zipOperation.compressionLevel", "value": "Fastest"}]),
        Step("v2", "zip: compress NoCompression", "zipOperation",
             {"operation": "compress", "source": RUN_DIR + r"\src",
              "destination": RUN_DIR + r"\store.zip",
              "compressionLevel": "NoCompression", "force": True}, target_machine=LOCAL,
             cases=[{"id": "zipOperation.compressionLevel.NoCompression",
                     "dimension": "zipOperation.compressionLevel",
                     "value": "NoCompression"}]),
        Step("v3", "zip: compress wildcard", "zipOperation",
             {"operation": "compress", "source": RUN_DIR + r"\src\*.txt",
              "destination": RUN_DIR + r"\wild.zip", "force": True},
             target_machine=LOCAL,
             cases=[{"id": "zipOperation.source.wildcard",
                     "dimension": "zipOperation.source",
                     "value": "wildcard in the final segment"},
                    {"id": "zipOperation.operation.default",
                     "dimension": "zipOperation.operation",
                     "value": "(absent) -> compress"}]),
        Step("v4", "zip: force overwrite", "zipOperation",
             {"operation": "compress", "source": RUN_DIR + r"\src",
              "destination": RUN_DIR + r"\opt.zip", "force": True},
             target_machine=LOCAL,
             cases=[{"id": "zipOperation.force.true", "assertedVia": "check", "dimension": "zipOperation.force",
                     "value": "true (overwrite)"}]),
        Step("v5", "zip: extract", "zipOperation",
             {"operation": "extract", "source": RUN_DIR + r"\opt.zip",
              "destination": RUN_DIR + r"\out", "force": True}, target_machine=LOCAL,
             cases=[{"id": "zipOperation.operation.extract", "assertedVia": "check",
                     "dimension": "zipOperation.operation", "value": "extract"}]),
        Step("check", "Verify round trip", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": SEED_HEAD +
                        "$out = Join-Path $dir 'out'\n"
                        "$found = @(Get-ChildItem -LiteralPath $out -Recurse -File).Count\n"
                        "$roundTripped = $found.ToString()\n"},
             target_machine=LOCAL),
        cleanup(),
        assert_step("""
$optSize   = {{v0.param.sizeBytes}}
$fastSize  = {{v1.param.sizeBytes}}
$storeSize = {{v2.param.sizeBytes}}
$wildSize  = {{v3.param.sizeBytes}}
$extracted = {{check.param.roundTripped}}

foreach ($pair in @(@('Optimal', $optSize), @('Fastest', $fastSize), @('NoCompression', $storeSize), @('wildcard', $wildSize))) {
  if ([int]$pair[1] -le 0) { throw "zipOperation $($pair[0]): archive size was $($pair[1])" }
}
# Stored entries keep their full size, so no-compression must be the largest archive.
if ([int]$storeSize -le [int]$optSize) {
  throw "NoCompression ($storeSize) should exceed Optimal ($optSize)"
}
if ([int]$extracted -ne 3) { throw "extract round trip: expected 3 files, found $extracted" }
$assertOk = 'zipOperation'
"""),
        ok_return("zipOperation", with_cid=True),
    ]
    return Workflow(
        19, "zipOperation", "[TestSuite] zipOperation",
        "Both operations, all three compression levels, a wildcard source, the absent "
        "operation default and a force overwrite, verified by a round trip.",
        "positive", "continuous", "B", steps, max_runtime=120)


def workflows():
    return [run_script_workflow(), file_operation_workflow(), folder_operation_workflow(),
            file_hash_workflow(), zip_operation_workflow()]
