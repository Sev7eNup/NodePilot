"""System-facing coverage: textFileEdit, registryOperation, wmiQuery, startProgram,
waitForCondition and the one power action that is safe to run."""

from suitelib import Step, Workflow, RUNS_ROOT, REG_ROOT
from spec_core import (LOCAL, janitor, cid, mkrun, cleanup, assert_step, ok_return,
                       CID, RUN_DIR)

SEED_HEAD = "$cid = " + CID + "\n$dir = Join-Path '" + RUNS_ROOT + "' $cid\n"
REG_KEY = REG_ROOT + "\\" + CID
REG_CLEANUP = ("$regKey = Join-Path '" + REG_ROOT + "' $cid\n"
               "Remove-Item -LiteralPath $regKey -Recurse -Force -ErrorAction SilentlyContinue\n")


def _edit(sid, label, config, cases=None):
    return Step(sid, label, "textFileEdit", config, target_machine=LOCAL, cases=cases)


# The thirteen edits below all act on the same file in sequence. Predicting the exact line
# count after each one is brittle, but the chain must stay continuous: what one operation
# reports as linesAfter is what the next one must see as linesBefore. An operation that
# silently did nothing, or one that wrote to the wrong file, breaks that link.
_SEQUENTIAL_EDITS = ["v0", "v1", "v2", "v3", "v4", "v5", "v6", "v7", "v8", "v9",
                     "v10", "v11", "v12"]
_INDEPENDENT_EDITS = ["e0", "e1", "e2", "e3", "e4", "e5", "l0", "l1", "l2"]


def _chain_continuity():
    lines = []
    for sid in _SEQUENTIAL_EDITS + _INDEPENDENT_EDITS:
        lines.append("$before_%s = {{%s.param.linesBefore}}" % (sid, sid))
        lines.append("$after_%s  = {{%s.param.linesAfter}}" % (sid, sid))
    for a, b in zip(_SEQUENTIAL_EDITS, _SEQUENTIAL_EDITS[1:]):
        lines.append(
            "if ([int]$after_%s -ne [int]$before_%s) { throw \"%s reported %s lines but %s saw %s\" }"
            % (a, b, a, "$after_" + a, b, "$before_" + b))
    for sid in _INDEPENDENT_EDITS:
        lines.append(
            "if ([int]$after_%s -lt 1) { throw \"%s produced an empty file\" }" % (sid, sid))
    return "\n".join(lines) + "\n"


def text_file_edit_workflow():
    main = RUN_DIR + r"\edit.txt"
    steps = [
        janitor(), cid(), mkrun(),
        Step("seed", "Seed: three lines", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": SEED_HEAD +
                        "$path = Join-Path $dir 'edit.txt'\n"
                        "Set-Content -LiteralPath $path -Value @('line1','line2','line3')\n"
                        "$seeded = 'ok'\n"},
             target_machine=LOCAL),
        _edit("v0", "tfe: append", {"operation": "append", "path": main, "content": "appended"},
              [{"id": "textFileEdit.operation.append",
                "dimension": "textFileEdit.operation", "value": "append"}]),
        _edit("v1", "tfe: prepend", {"operation": "prepend", "path": main, "content": "prepended"},
              [{"id": "textFileEdit.operation.prepend",
                "dimension": "textFileEdit.operation", "value": "prepend"}]),
        _edit("v2", "tfe: insert at line 3",
              {"operation": "insert", "path": main, "content": "inserted", "lineNumber": 3},
              [{"id": "textFileEdit.operation.insert",
                "dimension": "textFileEdit.operation", "value": "insert"},
               {"id": "textFileEdit.lineNumber", "dimension": "textFileEdit.lineNumber",
                "value": "1-based line"}]),
        _edit("v3", "tfe: replaceLine",
              {"operation": "replaceLine", "path": main, "content": "replaced-line",
               "lineNumber": 2},
              [{"id": "textFileEdit.operation.replaceLine",
                "dimension": "textFileEdit.operation", "value": "replaceLine"}]),
        _edit("v4", "tfe: replace all occurrences",
              {"operation": "replace", "path": main, "matchPattern": "line",
               "replace": "LINE", "occurrences": "all"},
              [{"id": "textFileEdit.operation.replace",
                "dimension": "textFileEdit.operation", "value": "replace"},
               {"id": "textFileEdit.occurrences.all",
                "dimension": "textFileEdit.occurrences", "value": "all"}]),
        _edit("v5", "tfe: replace first only, ignoreCase",
              {"operation": "replace", "path": main, "matchPattern": "line",
               "replace": "once", "occurrences": "first", "ignoreCase": True},
              [{"id": "textFileEdit.occurrences.first",
                "dimension": "textFileEdit.occurrences", "value": "first"},
               {"id": "textFileEdit.ignoreCase.true",
                "dimension": "textFileEdit.ignoreCase", "value": "true"}]),
        _edit("v6", "tfe: regex replace",
              {"operation": "replace", "path": main, "matchPattern": r"^LINE\d$",
               "replace": "regexed", "useRegex": True},
              [{"id": "textFileEdit.useRegex.true",
                "dimension": "textFileEdit.useRegex", "value": "true"}]),
        _edit("v7", "tfe: delete by lineNumber",
              {"operation": "delete", "path": main, "lineNumber": 1},
              [{"id": "textFileEdit.operation.delete-line",
                "dimension": "textFileEdit.operation", "value": "delete (lineNumber)"}]),
        _edit("v8", "tfe: delete by lineRange",
              {"operation": "delete", "path": main, "lineRange": [1, 2]},
              [{"id": "textFileEdit.lineRange", "dimension": "textFileEdit.lineRange",
                "value": "[from,to] delete selector"}]),
        _edit("v9", "tfe: delete by matchPattern",
              {"operation": "delete", "path": main, "matchPattern": "regexed"},
              [{"id": "textFileEdit.operation.delete-pattern",
                "dimension": "textFileEdit.operation", "value": "delete (matchPattern)"}]),
        _edit("v10", "tfe: appendIfMissing (twice, idempotent)",
              {"operation": "append", "path": main, "content": "only-once",
               "appendIfMissing": True},
              [{"id": "textFileEdit.appendIfMissing.true",
                "dimension": "textFileEdit.appendIfMissing", "value": "true"}]),
        _edit("v11", "tfe: appendIfMissing repeat",
              {"operation": "append", "path": main, "content": "only-once",
               "appendIfMissing": True, "appendIfMissingExact": True},
              [{"id": "textFileEdit.appendIfMissingExact.true",
                "dimension": "textFileEdit.appendIfMissingExact", "value": "true"}]),
        _edit("v12", "tfe: backup + dryRun",
              {"operation": "append", "path": main, "content": "never-written",
               "backupSuffix": ".bak", "dryRun": True},
              [{"id": "textFileEdit.backupSuffix", "dimension": "textFileEdit.backupSuffix",
                "value": ".bak"},
               {"id": "textFileEdit.dryRun.true", "dimension": "textFileEdit.dryRun",
                "value": "true"}]),
        # One file per encoding, each created by the activity itself.
        _edit("e0", "tfe: encoding utf8",
              {"operation": "append", "path": RUN_DIR + r"\enc-utf8.txt",
               "content": "abc", "encoding": "utf8", "createIfMissing": True},
              [{"id": "textFileEdit.encoding.utf8", "dimension": "textFileEdit.encoding",
                "value": "utf8"},
               {"id": "textFileEdit.createIfMissing.true",
                "dimension": "textFileEdit.createIfMissing", "value": "true"}]),
        _edit("e1", "tfe: encoding utf8-bom",
              {"operation": "append", "path": RUN_DIR + r"\enc-utf8bom.txt",
               "content": "abc", "encoding": "utf8-bom", "createIfMissing": True},
              [{"id": "textFileEdit.encoding.utf8-bom",
                "dimension": "textFileEdit.encoding", "value": "utf8-bom"}]),
        _edit("e2", "tfe: encoding utf16le",
              {"operation": "append", "path": RUN_DIR + r"\enc-16le.txt",
               "content": "abc", "encoding": "utf16le", "createIfMissing": True},
              [{"id": "textFileEdit.encoding.utf16le",
                "dimension": "textFileEdit.encoding", "value": "utf16le"}]),
        _edit("e3", "tfe: encoding utf16be",
              {"operation": "append", "path": RUN_DIR + r"\enc-16be.txt",
               "content": "abc", "encoding": "utf16be", "createIfMissing": True},
              [{"id": "textFileEdit.encoding.utf16be",
                "dimension": "textFileEdit.encoding", "value": "utf16be"}]),
        _edit("e4", "tfe: encoding ascii",
              {"operation": "append", "path": RUN_DIR + r"\enc-ascii.txt",
               "content": "abc", "encoding": "ascii", "createIfMissing": True},
              [{"id": "textFileEdit.encoding.ascii", "dimension": "textFileEdit.encoding",
                "value": "ascii"}]),
        _edit("e5", "tfe: encoding auto preserves the BOM",
              {"operation": "append", "path": RUN_DIR + r"\enc-utf8bom.txt",
               "content": "def", "encoding": "auto"},
              [{"id": "textFileEdit.encoding.auto", "dimension": "textFileEdit.encoding",
                "value": "auto (detect + preserve)"}]),
        _edit("l0", "tfe: lineEnding crlf",
              {"operation": "append", "path": RUN_DIR + r"\le-crlf.txt",
               "content": "one", "lineEnding": "crlf", "createIfMissing": True},
              [{"id": "textFileEdit.lineEnding.crlf",
                "dimension": "textFileEdit.lineEnding", "value": "crlf"}]),
        _edit("l1", "tfe: lineEnding lf",
              {"operation": "append", "path": RUN_DIR + r"\le-lf.txt",
               "content": "one", "lineEnding": "lf", "createIfMissing": True},
              [{"id": "textFileEdit.lineEnding.lf", "dimension": "textFileEdit.lineEnding",
                "value": "lf"}]),
        _edit("l2", "tfe: lineEnding preserve",
              {"operation": "append", "path": RUN_DIR + r"\le-lf.txt",
               "content": "two", "lineEnding": "preserve"},
              [{"id": "textFileEdit.lineEnding.preserve",
                "dimension": "textFileEdit.lineEnding", "value": "preserve"}]),
        Step("check", "Verify bytes on disk", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": SEED_HEAD + """
function Get-Head { param($name, $n) ,([IO.File]::ReadAllBytes((Join-Path $dir $name))[0..($n-1)]) }
$utf8Head   = (Get-Head 'enc-utf8.txt' 3) -join ','
$bomHead    = (Get-Head 'enc-utf8bom.txt' 3) -join ','
$le16Head   = (Get-Head 'enc-16le.txt' 2) -join ','
$be16Head   = (Get-Head 'enc-16be.txt' 2) -join ','
$asciiHead  = (Get-Head 'enc-ascii.txt' 3) -join ','
$bomText    = [IO.File]::ReadAllText((Join-Path $dir 'enc-utf8bom.txt'))
$crlfRaw    = [IO.File]::ReadAllText((Join-Path $dir 'le-crlf.txt'))
$lfRaw      = [IO.File]::ReadAllText((Join-Path $dir 'le-lf.txt'))
$crlfHasCr  = if ($crlfRaw.Contains([string][char]13)) { 'yes' } else { 'no' }
$lfHasCr    = if ($lfRaw.Contains([string][char]13)) { 'yes' } else { 'no' }
$bakExists  = if (Test-Path -LiteralPath (Join-Path $dir 'edit.txt.bak')) { 'yes' } else { 'no' }
$mainText   = [IO.File]::ReadAllText((Join-Path $dir 'edit.txt'))
$onlyOnce   = ([regex]::Matches($mainText, 'only-once')).Count.ToString()
$neverWritten = if ($mainText.Contains('never-written')) { 'yes' } else { 'no' }
"""},
             target_machine=LOCAL),
        cleanup(),
        assert_step(_chain_continuity() + """
$linesBefore = {{v0.param.linesBefore}}
$linesAfter  = {{v0.param.linesAfter}}
$encReported = {{e1.param.encoding}}
$leReported  = {{l0.param.lineEnding}}
$dryRun      = {{v12.param.dryRun}}
$utf8Head    = {{check.param.utf8Head}}
$bomHead     = {{check.param.bomHead}}
$le16Head    = {{check.param.le16Head}}
$be16Head    = {{check.param.be16Head}}
$asciiHead   = {{check.param.asciiHead}}
$bomText     = {{check.param.bomText}}
$crlfHasCr   = {{check.param.crlfHasCr}}
$lfHasCr     = {{check.param.lfHasCr}}
$bakExists   = {{check.param.bakExists}}
$onlyOnce    = {{check.param.onlyOnce}}
$neverWritten = {{check.param.neverWritten}}

if ([int]$linesAfter -ne ([int]$linesBefore + 1)) {
  throw "append should add exactly one line, went from $linesBefore to $linesAfter"
}
if ($encReported -ne 'utf8-bom') { throw "textFileEdit reported encoding '$encReported'" }
if ($leReported -ne 'crlf') { throw "textFileEdit reported lineEnding '$leReported'" }
if ($dryRun -ne 'True' -and $dryRun -ne 'true') { throw "dryRun flag not reported: '$dryRun'" }
if ($utf8Head -eq '239,187,191') { throw "encoding utf8 must not write a BOM" }
if ($bomHead -ne '239,187,191') { throw "encoding utf8-bom head was '$bomHead'" }
if ($le16Head -ne '255,254') { throw "encoding utf16le head was '$le16Head'" }
if ($be16Head -ne '254,255') { throw "encoding utf16be head was '$be16Head'" }
if ($asciiHead -eq '239,187,191') { throw "encoding ascii must not write a BOM" }
if ($bomText -notmatch 'def') { throw "encoding auto did not append to the BOM file" }
if ($crlfHasCr -ne 'yes') { throw "lineEnding crlf produced no carriage return" }
if ($lfHasCr -ne 'no') { throw "lineEnding lf leaked a carriage return" }
if ($bakExists -eq 'yes') { throw "a dry run must not leave a backup behind" }
if ($neverWritten -ne 'no') { throw "a dry run must not modify the file" }
if ([int]$onlyOnce -ne 1) { throw "appendIfMissing wrote the line $onlyOnce times" }
$assertOk = 'textFileEdit'
"""),
        ok_return("textFileEdit", with_cid=True),
    ]
    return Workflow(
        25, "textFileEdit", "[TestSuite] textFileEdit",
        "All six operations including both alternative delete selectors, all six "
        "encodings verified by the bytes on disk, all three line endings, both "
        "occurrence modes, regex matching, the idempotent append and a dry run.",
        "positive", "continuous", "A", steps, max_runtime=120, nodes_per_row=7,
        excluded=[
            {"id": "textFileEdit.maxFileSizeMB", "dimension": "textFileEdit.maxFileSizeMB",
             "value": "per-step size cap",
             "reason": "Reaching the cap means writing a file of tens of megabytes on "
                       "every cadence. The key is also missing from the config reference, "
                       "which PR 5 fixes.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/TextFileEditActivityTests.cs"},
        ])


def registry_workflow():
    steps = [
        janitor(), cid(),
        Step("v0", "reg: createKey", "registryOperation",
             {"operation": "createKey", "keyPath": REG_KEY}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.createKey", "assertedVia": "v10",
                     "dimension": "registryOperation.operation", "value": "createKey"}]),
        Step("v1", "reg: write String", "registryOperation",
             {"operation": "write", "keyPath": REG_KEY, "valueName": "Str",
              "value": "suite", "valueType": "String"}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.write",
                     "dimension": "registryOperation.operation", "value": "write"},
                    {"id": "registryOperation.valueType.String",
                     "dimension": "registryOperation.valueType", "value": "String"}]),
        Step("v2", "reg: write ExpandString", "registryOperation",
             {"operation": "write", "keyPath": REG_KEY, "valueName": "Expand",
              "value": "%SystemRoot%\\System32", "valueType": "ExpandString"},
             target_machine=LOCAL,
             cases=[{"id": "registryOperation.valueType.ExpandString", "assertedVia": "v8",
                     "dimension": "registryOperation.valueType", "value": "ExpandString"}]),
        Step("v3", "reg: write DWord", "registryOperation",
             {"operation": "write", "keyPath": REG_KEY, "valueName": "Num",
              "value": "42", "valueType": "DWord"}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.valueType.DWord", "assertedVia": "v8",
                     "dimension": "registryOperation.valueType", "value": "DWord"}]),
        Step("v4", "reg: write QWord", "registryOperation",
             {"operation": "write", "keyPath": REG_KEY, "valueName": "Big",
              "value": "1234567890123", "valueType": "QWord"}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.valueType.QWord", "assertedVia": "v8",
                     "dimension": "registryOperation.valueType", "value": "QWord"}]),
        Step("v5", "reg: write Binary", "registryOperation",
             {"operation": "write", "keyPath": REG_KEY, "valueName": "Bin",
              "value": "DEADBEEF", "valueType": "Binary"}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.valueType.Binary", "assertedVia": "v8",
                     "dimension": "registryOperation.valueType", "value": "Binary"}]),
        Step("v6", "reg: write MultiString", "registryOperation",
             {"operation": "write", "keyPath": REG_KEY, "valueName": "Multi",
              "value": "one\ntwo", "valueType": "MultiString"}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.valueType.MultiString", "assertedVia": "v8",
                     "dimension": "registryOperation.valueType",
                     "value": "MultiString"}]),
        Step("v7", "reg: read one value", "registryOperation",
             {"operation": "read", "keyPath": REG_KEY, "valueName": "Str"},
             target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.read-single",
                     "dimension": "registryOperation.operation",
                     "value": "read (with valueName)"}]),
        Step("v8", "reg: read all values", "registryOperation",
             {"operation": "read", "keyPath": REG_KEY}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.read-all",
                     "dimension": "registryOperation.operation",
                     "value": "read (without valueName)"}]),
        Step("v9", "reg: exists value", "registryOperation",
             {"operation": "exists", "keyPath": REG_KEY, "valueName": "Num"},
             target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.exists-value",
                     "dimension": "registryOperation.operation",
                     "value": "exists (with valueName)"}]),
        Step("v10", "reg: exists key", "registryOperation",
             {"operation": "exists", "keyPath": REG_KEY}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.exists-key",
                     "dimension": "registryOperation.operation",
                     "value": "exists (without valueName)"}]),
        Step("v11", "reg: listValues", "registryOperation",
             {"operation": "listValues", "keyPath": REG_KEY}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.listValues",
                     "dimension": "registryOperation.operation", "value": "listValues"}]),
        Step("v12", "reg: listSubKeys", "registryOperation",
             {"operation": "listSubKeys", "keyPath": REG_ROOT}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.listSubKeys",
                     "dimension": "registryOperation.operation", "value": "listSubKeys"}]),
        Step("v13", "reg: deleteValue", "registryOperation",
             {"operation": "deleteValue", "keyPath": REG_KEY, "valueName": "Str"},
             target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.deleteValue", "assertedVia": "v14",
                     "dimension": "registryOperation.operation", "value": "deleteValue"}]),
        Step("v14", "reg: exists after deleteValue", "registryOperation",
             {"operation": "exists", "keyPath": REG_KEY, "valueName": "Str"},
             target_machine=LOCAL),
        Step("v15", "reg: deleteKey", "registryOperation",
             {"operation": "deleteKey", "keyPath": REG_KEY}, target_machine=LOCAL,
             cases=[{"id": "registryOperation.operation.deleteKey", "assertedVia": "v16",
                     "dimension": "registryOperation.operation", "value": "deleteKey"}]),
        Step("v16", "reg: exists after deleteKey", "registryOperation",
             {"operation": "exists", "keyPath": REG_KEY}, target_machine=LOCAL),
        cleanup(REG_CLEANUP),
        assert_step("""
$readOne   = {{v7.param.value}}
$readType  = {{v7.param.type}}
$allValues = {{v8.param.values}}
$allCount  = {{v8.param.count}}
$existsVal = {{v9.param.exists}}
$existsKey = {{v10.param.exists}}
$listCount = {{v11.param.count}}
$subKeys   = {{v12.param.subKeys}}
$goneAfterDelete = {{v14.param.exists}}
$keyGone         = {{v16.param.exists}}

if ($readOne -ne 'suite') { throw "registry read: expected 'suite', got '$readOne'" }
if ($readType -notmatch 'String') { throw "registry read type: '$readType'" }
if ([int]$allCount -ne 6) { throw "read without valueName should return all 6 values, got $allCount" }
foreach ($name in @('Str','Expand','Num','Big','Bin','Multi')) {
  if ($allValues -notmatch $name) { throw "registry read-all is missing '$name'" }
}
if ($existsVal -ne 'True' -and $existsVal -ne 'true') { throw "exists(value): '$existsVal'" }
if ($existsKey -ne 'True' -and $existsKey -ne 'true') { throw "exists(key): '$existsKey'" }
if ([int]$listCount -ne 6) { throw "listValues count: $listCount" }
if ([string]::IsNullOrWhiteSpace($subKeys)) { throw "listSubKeys returned nothing" }
if ($goneAfterDelete -eq 'True' -or $goneAfterDelete -eq 'true') {
  throw "the value should be gone after deleteValue"
}
if ($keyGone -eq 'True' -or $keyGone -eq 'true') { throw "the key should be gone after deleteKey" }
$assertOk = 'registryOperation'
"""),
        ok_return("registryOperation", with_cid=True),
    ]
    return Workflow(
        26, "registryOperation", "[TestSuite] registryOperation",
        "All eight operations including both shapes of read and exists, and all six "
        "value types, under a per-run key below HKCU.",
        "positive", "continuous", "A", steps, max_runtime=120, nodes_per_row=7)


def wmi_workflow():
    steps = [
        Step("v0", "wmi: query + captureProperties", "wmiQuery",
             {"mode": "query", "className": "Win32_OperatingSystem",
              "namespace": "root\\cimv2", "captureProperties": ["Caption", "Version"]},
             target_machine=LOCAL,
             cases=[{"id": "wmiQuery.mode.query", "dimension": "wmiQuery.mode",
                     "value": "query"},
                    {"id": "wmiQuery.captureProperties",
                     "dimension": "wmiQuery.captureProperties",
                     "value": "JSON array of property names"}]),
        Step("v1", "wmi: query + filter", "wmiQuery",
             {"mode": "query", "className": "Win32_LogicalDisk",
              "filter": "DriveType=3", "namespace": "root\\cimv2",
              "captureProperties": ["DeviceID"]}, target_machine=LOCAL,
             cases=[{"id": "wmiQuery.filter", "dimension": "wmiQuery.filter",
                     "value": "WHERE clause"}]),
        Step("v2", "wmi: raw WQL", "wmiQuery",
             {"mode": "wql",
              "query": "SELECT Name, NumberOfLogicalProcessors FROM Win32_ComputerSystem",
              "namespace": "root\\cimv2", "captureProperties": ["Name"]},
             target_machine=LOCAL,
             cases=[{"id": "wmiQuery.mode.wql", "dimension": "wmiQuery.mode",
                     "value": "wql"}]),
        Step("v3", "wmi: instance method", "wmiQuery",
             {"mode": "invokeMethod", "className": "Win32_Process",
              "methodName": "GetOwner", "filter": "Name='explorer.exe'",
              "namespace": "root\\cimv2", "captureProperties": ["ReturnValue"]},
             target_machine=LOCAL,
             cases=[{"id": "wmiQuery.mode.invokeMethod-instance",
                     "dimension": "wmiQuery.mode",
                     "value": "invokeMethod (instance, filter scoped)"}]),
        # A static call in a second namespace: StdRegProv.EnumKey is read-only. hDefKey is
        # left out on purpose - every HKEY root exceeds Int32.MaxValue, so a JSON number
        # reaches CIM as an Int64 and the uint32 parameter rejects it. Omitted, the
        # provider defaults to HKEY_LOCAL_MACHINE.
        Step("v4", "wmi: static method, root\\default", "wmiQuery",
             {"mode": "invokeMethod", "className": "StdRegProv", "methodName": "EnumKey",
              "namespace": "root\\default",
              "arguments": {"sSubKeyName": "SOFTWARE"},
              "captureProperties": ["ReturnValue"]}, target_machine=LOCAL,
             cases=[{"id": "wmiQuery.mode.invokeMethod-static",
                     "dimension": "wmiQuery.mode", "value": "invokeMethod (static)"},
                    {"id": "wmiQuery.namespace.non-default",
                     "dimension": "wmiQuery.namespace", "value": "root\\default"},
                    {"id": "wmiQuery.arguments", "dimension": "wmiQuery.arguments",
                     "value": "JSON object -> PS hashtable"}]),
        Step("v5", "wmi: no captureProperties", "wmiQuery",
             {"mode": "query", "className": "Win32_BIOS", "namespace": "root\\cimv2"},
             target_machine=LOCAL,
             cases=[{"id": "wmiQuery.captureProperties.absent",
                     "dimension": "wmiQuery.captureProperties",
                     "value": "(absent) -> no param.* projection"}]),
        assert_step("""
$caption = {{v0.param.Caption}}
$version = {{v0.param.Version}}
$osCount = {{v0.param.count}}
$disk    = {{v1.param.DeviceID}}
$csName  = {{v2.param.Name}}
$ownerRc = {{v3.param.ReturnValue}}
$regRc   = {{v4.param.ReturnValue}}
$biosOut = {{v5.output}}

if ($caption -notmatch 'Windows') { throw "wmi Caption: '$caption'" }
if ([string]::IsNullOrWhiteSpace($version)) { throw "wmi Version was not captured" }
if ([int]$osCount -ne 1) { throw "Win32_OperatingSystem count: $osCount" }
if ($disk -notmatch ':') { throw "filtered DriveType=3 query returned '$disk'" }
if ([string]::IsNullOrWhiteSpace($csName)) { throw "wql Name was not captured" }
if ($ownerRc -ne '0') { throw "GetOwner ReturnValue: '$ownerRc'" }
if ($regRc -ne '0') { throw "StdRegProv.EnumKey ReturnValue: '$regRc'" }
if ([string]::IsNullOrWhiteSpace($biosOut)) { throw "a query without captureProperties still returns output" }
$assertOk = 'wmiQuery'
"""),
        ok_return("wmiQuery"),
    ]
    return Workflow(
        27, "wmiQuery", "[TestSuite] wmiQuery",
        "All three modes including a static method in a second namespace, a WHERE "
        "filter, method arguments and the projection that captureProperties drives.",
        "positive", "continuous", "A", steps, max_runtime=90)


CMD = r"C:\Windows\System32\cmd.exe"


def start_program_workflow():
    steps = [
        janitor(), cid(), mkrun(),
        Step("v0", "prog: wait, exit 0, stdout", "startProgram",
             {"filePath": CMD, "arguments": "/c echo suite-marker",
              "workingDirectory": r"C:\Windows\System32", "waitForExit": True,
              "timeoutSeconds": 30, "successExitCodes": "0"}, target_machine=LOCAL,
             cases=[{"id": "startProgram.waitForExit.true",
                     "dimension": "startProgram.waitForExit", "value": "true"},
                    {"id": "startProgram.arguments", "dimension": "startProgram.arguments",
                     "value": "command line"},
                    {"id": "startProgram.workingDirectory",
                     "dimension": "startProgram.workingDirectory", "value": "absolute"}]),
        Step("v1", "prog: exit 3 accepted", "startProgram",
             {"filePath": CMD, "arguments": "/c exit 3", "waitForExit": True,
              "timeoutSeconds": 30, "successExitCodes": "0,3"}, target_machine=LOCAL,
             cases=[{"id": "startProgram.successExitCodes.non-zero",
                     "dimension": "startProgram.successExitCodes", "value": "0,3"}]),
        Step("v2", "prog: stderr capture", "startProgram",
             {"filePath": CMD, "arguments": "/c echo oops 1>&2", "waitForExit": True,
              "timeoutSeconds": 30, "successExitCodes": "0"}, target_machine=LOCAL,
             cases=[{"id": "startProgram.stderr", "dimension": "startProgram.output",
                     "value": "stderr captured"}]),
        Step("v3", "prog: fire and forget", "startProgram",
             {"filePath": CMD, "arguments": "/c echo background", "waitForExit": False,
              "timeoutSeconds": 30}, target_machine=LOCAL,
             cases=[{"id": "startProgram.waitForExit.false",
                     "dimension": "startProgram.waitForExit", "value": "false"}]),
        cleanup(),
        assert_step("""
$stdout   = {{v0.param.stdout}}
$exit0    = {{v0.param.exitCode}}
$waited   = {{v0.param.waited}}
$exit3    = {{v1.param.exitCode}}
$stderr   = {{v2.param.stderr}}
$bgPid    = {{v3.param.processId}}
$bgWaited = {{v3.param.waited}}

if ($stdout -notmatch 'suite-marker') { throw "startProgram stdout: '$stdout'" }
if ($exit0 -ne '0') { throw "startProgram exit code: '$exit0'" }
if ($waited -ne 'True' -and $waited -ne 'true') { throw "waited flag: '$waited'" }
if ($exit3 -ne '3') { throw "successExitCodes: expected exit 3 reported, got '$exit3'" }
if ($stderr -notmatch 'oops') { throw "startProgram stderr: '$stderr'" }
if ([string]::IsNullOrWhiteSpace($bgPid)) { throw "fire-and-forget returned no process id" }
if ($bgWaited -eq 'True' -or $bgWaited -eq 'true') { throw "fire-and-forget should not report waited" }
$assertOk = 'startProgram'
"""),
        ok_return("startProgram", with_cid=True),
    ]
    return Workflow(
        28, "startProgram", "[TestSuite] startProgram",
        "Both wait modes, stdout and stderr capture, a non-zero exit code accepted "
        "through successExitCodes and an explicit working directory.",
        "positive", "continuous", "A", steps, max_runtime=90,
        excluded=[
            {"id": "startProgram.useShellExecute.true",
             "dimension": "startProgram.useShellExecute", "value": "true",
             "reason": "Blocked by StartProgram:DisallowShellExecute, which defaults to "
                       "on in production. The rejection is exercised by the hardened "
                       "negative workflow instead.",
             "coveredBy": "scripts/test-suite/negative/85-hardening.json"},
        ])


def wait_for_condition_workflow():
    steps = [
        janitor(), cid(), mkrun(),
        Step("disc", "Discover the API's own listening port", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": """
# waitForCondition probes must target something that is genuinely listening. The script
# runs inside the API process, so the process itself is the only reliable fixture.
$ownPid = [System.Diagnostics.Process]::GetCurrentProcess().Id
$listener = Get-NetTCPConnection -State Listen -OwningProcess $ownPid -ErrorAction SilentlyContinue |
  Sort-Object LocalPort | Select-Object -First 1
if (-not $listener) { throw 'could not discover a listening port for the API process' }
$selfPort = $listener.LocalPort.ToString()
"""},
             target_machine=LOCAL),
        Step("v0", "wait: script expression", "waitForCondition",
             {"conditionType": "script", "script": "$true", "intervalSeconds": 1,
              "timeoutSeconds": 15}, target_machine=LOCAL,
             cases=[{"id": "waitForCondition.conditionType.script",
                     "dimension": "waitForCondition.conditionType", "value": "script"},
                    {"id": "waitForCondition.intervalSeconds",
                     "dimension": "waitForCondition.intervalSeconds", "value": "1"}]),
        Step("v1", "wait: pathExists", "waitForCondition",
             {"conditionType": "pathExists", "path": r"C:\Windows",
              "intervalSeconds": 1, "timeoutSeconds": 15}, target_machine=LOCAL,
             cases=[{"id": "waitForCondition.conditionType.pathExists",
                     "dimension": "waitForCondition.conditionType",
                     "value": "pathExists"}]),
        Step("v2", "wait: serviceRunning", "waitForCondition",
             {"conditionType": "serviceRunning", "serviceName": "EventLog",
              "intervalSeconds": 1, "timeoutSeconds": 15}, target_machine=LOCAL,
             cases=[{"id": "waitForCondition.conditionType.serviceRunning",
                     "dimension": "waitForCondition.conditionType",
                     "value": "serviceRunning"}]),
        Step("v3", "wait: portOpen (own port)", "waitForCondition",
             {"conditionType": "portOpen", "host": "localhost",
              "port": "{{disc.param.selfPort}}", "intervalSeconds": 1,
              "timeoutSeconds": 15}, target_machine=LOCAL,
             cases=[{"id": "waitForCondition.conditionType.portOpen",
                     "dimension": "waitForCondition.conditionType", "value": "portOpen"}]),
        Step("v4", "wait: httpOk", "waitForCondition",
             {"conditionType": "httpOk", "url": "{{globals.NP_TESTSUITE_SELF_URL}}",
              "intervalSeconds": 1, "timeoutSeconds": 15}, target_machine=LOCAL,
             cases=[{"id": "waitForCondition.conditionType.httpOk",
                     "dimension": "waitForCondition.conditionType", "value": "httpOk",
                     "requires": ["globals:NP_TESTSUITE_SELF_URL"]}]),
        cleanup(),
        assert_step("""
$scriptAttempts = {{v0.param.attempts}}
$scriptElapsed  = {{v0.param.elapsedSeconds}}
$pathResult     = {{v1.param.lastResult}}
$svcAttempts    = {{v2.param.attempts}}
$portAttempts   = {{v3.param.attempts}}
$httpAttempts   = {{v4.param.attempts}}

# A condition that is already true must settle on the first poll, not burn the interval.
if ([int]$scriptAttempts -lt 1) { throw "waitForCondition script attempts: $scriptAttempts" }
if ([int]$scriptElapsed -gt 10) { throw "an immediately true condition took $scriptElapsed s" }
if ([string]::IsNullOrWhiteSpace($pathResult)) { throw "pathExists reported no lastResult" }
if ([int]$svcAttempts -lt 1)  { throw "serviceRunning attempts: $svcAttempts" }
if ([int]$portAttempts -lt 1) { throw "portOpen attempts: $portAttempts" }
if ([int]$httpAttempts -lt 1) { throw "httpOk attempts: $httpAttempts" }
$assertOk = 'waitForCondition'
"""),
        ok_return("waitForCondition", with_cid=True),
    ]
    return Workflow(
        29, "waitForCondition", "[TestSuite] waitForCondition",
        "All five condition types. The port and URL probes target the API's own "
        "listener, discovered at run time, so the workflow is not tied to one port.",
        "positive", "continuous", "A", steps, max_runtime=120,
        requires=["globals:NP_TESTSUITE_SELF_URL"])


def power_management_workflow():
    steps = [
        Step("v0", "power: abort (no-op)", "powerManagement", {"action": "abort"},
             target_machine=LOCAL,
             cases=[{"id": "powerManagement.action.abort",
                     "dimension": "powerManagement.action", "value": "abort"}]),
        assert_step("""
$out = {{v0.output}}
# `shutdown /a` exits 1116 when there is nothing to abort; the activity treats that as
# success on purpose, which is exactly the path a scheduled no-op takes.
if ([string]::IsNullOrWhiteSpace($out)) { throw "powerManagement abort produced no output" }
$assertOk = 'powerManagement'
"""),
        ok_return("powerManagement"),
    ]
    return Workflow(
        33, "powerManagement", "[TestSuite] powerManagement",
        "The only power action that can run on a cadence without taking the host down.",
        "positive", "continuous", "B", steps, max_runtime=45,
        excluded=[
            {"id": "powerManagement.action.shutdown",
             "dimension": "powerManagement.action", "value": "shutdown",
             "reason": "Would take the host down. Also blocked against localhost unless "
                       "PowerManagement:AllowLocalSelfShutdown is set; that rejection is "
                       "in the negative contract.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/PowerManagementActivityTests.cs"},
            {"id": "powerManagement.action.restart",
             "dimension": "powerManagement.action", "value": "restart",
             "reason": "Would take the host down.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/PowerManagementActivityTests.cs"},
            {"id": "powerManagement.action.hibernate",
             "dimension": "powerManagement.action", "value": "hibernate",
             "reason": "Would suspend the host.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/PowerManagementActivityTests.cs"},
            {"id": "powerManagement.action.logoff",
             "dimension": "powerManagement.action", "value": "logoff",
             "reason": "Ends the interactive session the developer is working in; the "
                       "self-shutdown guard does not cover logoff, so nothing would stop it.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/PowerManagementActivityTests.cs"},
            {"id": "powerManagement.delaySeconds", "dimension": "powerManagement.delaySeconds",
             "value": "delay before the action",
             "reason": "Only meaningful together with shutdown or restart, which are "
                       "excluded above.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/PowerManagementActivityTests.cs"},
            {"id": "powerManagement.message", "dimension": "powerManagement.message",
             "value": "message to logged-on users",
             "reason": "Only meaningful together with shutdown or restart.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/PowerManagementActivityTests.cs"},
            {"id": "powerManagement.force", "dimension": "powerManagement.force",
             "value": "force applications closed",
             "reason": "Only meaningful together with shutdown or restart.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/PowerManagementActivityTests.cs"},
        ])


def workflows():
    return [text_file_edit_workflow(), registry_workflow(), wmi_workflow(),
            start_program_workflow(), wait_for_condition_workflow(),
            power_management_workflow()]
