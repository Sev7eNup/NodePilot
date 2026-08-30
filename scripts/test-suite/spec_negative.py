"""The negative contract: workflows whose expected terminal status is Failed.

The engine has no notion of a handled failure - it counts failed steps at the end and any
non-zero count makes the whole execution Failed. So a deliberate failure cannot live in a
workflow that is supposed to stay green, and "the run failed" on its own proves nothing.
Verify-TestSuite compares the set of failed step ids against the manifest, so a run that
fails for an unexpected reason is reported as a defect rather than a pass.

Each workflow is a linear chain joined by Always edges: every declared node fails, the
chain keeps going, and the supporting nodes around them must still succeed.
"""

from suitelib import Step, Workflow, RUNS_ROOT
from spec_core import LOCAL, janitor, cid, mkrun, ret, CID, RUN_DIR

API_URL = "{{globals.NP_TESTSUITE_API_URL}}"
XML_DTD = ('<?xml version="1.0"?><!DOCTYPE root [<!ENTITY x "expanded">]>'
           '<root><item>&x;</item></root>')


def _neg(sid, label, activity, config, case_id, dimension, value, error_contains,
         target=None):
    return Step(sid, label, activity, config, target_machine=target,
                cases=[{"id": case_id, "dimension": dimension, "value": value,
                        "expectedFailure": {"stepId": sid,
                                            "errorContains": error_contains}}])


def filesystem_negative():
    steps = [
        janitor(), cid(), mkrun(),
        Step("seed", "Seed: a file and an archive", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": "$cid = " + CID + "\n"
                        "$dir = Join-Path '" + RUNS_ROOT + "' $cid\n"
                        "Set-Content -LiteralPath (Join-Path $dir 'plain.txt') -Value 'content'\n"
                        "Compress-Archive -Path (Join-Path $dir 'plain.txt') "
                        "-DestinationPath (Join-Path $dir 'taken.zip') -Force\n"
                        "$seeded = 'ok'\n"},
             target_machine=LOCAL),
        _neg("n0", "fileOp: delete a directory", "fileOperation",
             {"operation": "delete", "path": RUN_DIR},
             "fileOperation.guard.leaf-only", "fileOperation.path",
             "directory rejected by the file-only guard", "", LOCAL),
        _neg("n1", "fileOp: copy a missing source", "fileOperation",
             {"operation": "copy", "path": RUN_DIR + r"\absent.txt",
              "destination": RUN_DIR + r"\copy.txt"},
             "fileOperation.guard.missing-source", "fileOperation.path",
             "missing source", "", LOCAL),
        _neg("n2", "folderOp: delete a file", "folderOperation",
             {"operation": "delete", "path": RUN_DIR + r"\plain.txt"},
             "folderOperation.guard.container-only", "folderOperation.path",
             "file rejected by the folder-only guard", "", LOCAL),
        _neg("n3", "hash: expected mismatch", "fileHash",
             {"path": RUN_DIR + r"\plain.txt", "algorithm": "SHA256",
              "expected": "0" * 64},
             "fileHash.expected.mismatch", "fileHash.expected",
             "mismatching hash", "", LOCAL),
        _neg("n4", "zip: refuse to overwrite", "zipOperation",
             {"operation": "compress", "source": RUN_DIR + r"\plain.txt",
              "destination": RUN_DIR + r"\taken.zip", "force": False},
             "zipOperation.force.false", "zipOperation.force",
             "false (target exists)", "", LOCAL),
        _neg("n5", "tfe: two delete selectors", "textFileEdit",
             {"operation": "delete", "path": RUN_DIR + r"\plain.txt",
              "lineNumber": 1, "matchPattern": "content"},
             "textFileEdit.delete.ambiguous-selector", "textFileEdit.operation",
             "delete with two selectors", "", LOCAL),
        Step("cleanup", "Cleanup: run sandbox", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": "$cid = " + CID + "\n"
                        "Remove-Item -LiteralPath (Join-Path '" + RUNS_ROOT + "' $cid) "
                        "-Recurse -Force -ErrorAction SilentlyContinue\n$cleanupDone = 'ok'\n"},
             target_machine=LOCAL),
        ret({"contract": "negative", "area": "filesystem"}),
    ]
    return Workflow(
        80, "filesystem", "[TestSuite-Neg] filesystem",
        "Six filesystem guards that must reject: file-only and folder-only operations, a "
        "missing source, a hash mismatch, a refused overwrite and an ambiguous delete.",
        "negative", "continuous", "B", steps, max_runtime=90)


def remote_negative():
    steps = [
        _neg("n0", "prog: relative filePath", "startProgram",
             {"filePath": "cmd.exe", "arguments": "/c echo x", "waitForExit": True,
              "timeoutSeconds": 20},
             "startProgram.filePath.relative", "startProgram.filePath",
             "bare name (PATH is not searched)", "", LOCAL),
        _neg("n1", "prog: exit 1 not accepted", "startProgram",
             {"filePath": r"C:\Windows\System32\cmd.exe", "arguments": "/c exit 1",
              "waitForExit": True, "timeoutSeconds": 20, "successExitCodes": "0"},
             "startProgram.successExitCodes.rejected", "startProgram.successExitCodes",
             "exit outside the accepted set", "", LOCAL),
        _neg("n2", "reg: prefix outside the whitelist", "registryOperation",
             {"operation": "createKey", "keyPath": r"C:\Windows"},
             "registryOperation.guard.prefix", "registryOperation.keyPath",
             "path outside the registry-root whitelist", "", LOCAL),
        _neg("n3", "wmi: reserved capture name", "wmiQuery",
             {"mode": "query", "className": "Win32_BIOS", "namespace": "root\\cimv2",
              "captureProperties": ["count"]},
             "wmiQuery.captureProperties.reserved", "wmiQuery.captureProperties",
             "reserved name 'count'", "", LOCAL),
        _neg("n4", "wmi: captureProperties as a string", "wmiQuery",
             {"mode": "query", "className": "Win32_BIOS", "namespace": "root\\cimv2",
              "captureProperties": "Caption,Version"},
             "wmiQuery.captureProperties.not-array", "wmiQuery.captureProperties",
             "comma-separated string instead of an array", "", LOCAL),
        _neg("n5", "wmi: unknown class", "wmiQuery",
             {"mode": "query", "className": "Win32_NoSuchClassHere",
              "namespace": "root\\cimv2", "captureProperties": ["Name"]},
             "wmiQuery.className.unknown", "wmiQuery.className",
             "unknown class", "", LOCAL),
        _neg("n6", "wait: template in the probe script", "waitForCondition",
             {"conditionType": "script", "script": "{{nosuchstep.output}} -eq 1",
              "intervalSeconds": 1, "timeoutSeconds": 10},
             "waitForCondition.script.template-rejected", "waitForCondition.script",
             "{{...}} rejected as an injection vector", "", LOCAL),
        _neg("n7", "wait: never satisfied", "waitForCondition",
             {"conditionType": "script", "script": "$false", "intervalSeconds": 1,
              "timeoutSeconds": 5},
             "waitForCondition.timeout", "waitForCondition.timeoutSeconds",
             "condition never becomes true", "", LOCAL),
        ret({"contract": "negative", "area": "remote"}),
    ]
    return Workflow(
        81, "remote", "[TestSuite-Neg] remote",
        "Eight rejections on the PowerShell-backed activities: path shape, exit-code "
        "policy, the registry-root whitelist, captureProperties validation, an unknown "
        "CIM class, the probe-script injection guard and a poll timeout.",
        "negative", "continuous", "B", steps, max_runtime=90)


def engine_local_negative():
    def rest(sid, label, method, case_id, value, url=API_URL):
        return _neg(sid, label, "restApi",
                    {"url": url, "method": method, "timeoutSeconds": 10},
                    case_id, "restApi.method", value, "")

    steps = [
        # restApi treats any non-2xx as a failed step. GET on this route answers 401 while
        # the write verbs fall through to the /api catch-all and answer 404 - different
        # statuses, which is what proves the verb actually left the client.
        rest("n0", "rest: PUT", "PUT", "restApi.method.PUT", "PUT (non-2xx)"),
        rest("n1", "rest: PATCH", "PATCH", "restApi.method.PATCH", "PATCH (non-2xx)"),
        rest("n2", "rest: DELETE", "DELETE", "restApi.method.DELETE", "DELETE (non-2xx)"),
        rest("n3", "rest: POST unauthenticated", "POST", "restApi.method.POST",
             "POST (401 without a token)"),
        _neg("n4", "rest: unknown path", "restApi",
             {"url": "{{globals.NP_TESTSUITE_SELF_URL}}/no-such-segment",
              "method": "GET", "timeoutSeconds": 10},
             "restApi.status.non-2xx", "restApi.url", "404 fails the step", ""),
        _neg("n5", "sql: template in the query", "sql",
             {"provider": "sqlite", "dataSource": ":memory:",
              "query": "SELECT '{{nosuchstep.output}}' AS x", "timeoutSeconds": 10},
             "sql.query.template-rejected", "sql.query",
             "{{...}} rejected; bind through parameters instead", ""),
        _neg("n6", "sql: syntax error", "sql",
             {"provider": "sqlite", "dataSource": ":memory:",
              "query": "SELCT 1", "timeoutSeconds": 10},
             "sql.query.syntax-error", "sql.query", "malformed statement", ""),
        _neg("n7", "xml: document type declaration", "xmlQuery",
             {"source": "inline", "content": XML_DTD, "xpath": "//item",
              "resultMode": "all"},
             "xmlQuery.guard.dtd", "xmlQuery.content",
             "DTD rejected (XXE / entity expansion)", ""),
        _neg("n8", "json: malformed document", "jsonQuery",
             {"source": "inline", "content": '{"items":[', "jsonPath": "$.items[*]",
              "resultMode": "all"},
             "jsonQuery.content.malformed", "jsonQuery.content", "malformed JSON", ""),
        ret({"contract": "negative", "area": "engine-local"}),
    ]
    return Workflow(
        82, "engine-local", "[TestSuite-Neg] engine-local",
        "The four write verbs (restApi fails on any non-2xx), an unknown path, the SQL "
        "template guard, a syntax error, a DTD and malformed JSON.",
        "negative", "continuous", "B", steps, max_runtime=90,
        requires=["globals:NP_TESTSUITE_API_URL", "globals:NP_TESTSUITE_SELF_URL"])


CHILD = "[TestSuite] Child: Echo Item"


def control_flow_negative():
    steps = [
        _neg("n0", "startWf: child does not exist", "startWorkflow",
             {"workflowNameOrId": "[TestSuite] No Such Child", "waitForCompletion": True,
              "timeoutSeconds": 30},
             "startWorkflow.child.missing", "startWorkflow.workflowNameOrId",
             "unresolvable child", ""),
        _neg("n1", "forEach: stop on first error", "forEach",
             {"items": '["ok1","boom","ok2"]', "itemsFormat": "json",
              "childWorkflowNameOrId": CHILD, "maxParallelism": 1,
              "continueOnError": False, "timeoutSecondsPerItem": 60},
             "forEach.continueOnError.false", "forEach.continueOnError",
             "false (a failing item fails the step)", ""),
        _neg("n2", "forEach: reserved parameter prefix", "forEach",
             {"items": '["x"]', "itemsFormat": "json", "childWorkflowNameOrId": CHILD,
              "itemParameterName": "__callDepth", "maxParallelism": 1,
              "timeoutSecondsPerItem": 30},
             "forEach.itemParameterName.reserved", "forEach.itemParameterName",
             "__ prefix is reserved", ""),
        Step("big", "Seed: five oversized values", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": "$blobA = 'a' * 9000\n$blobB = 'b' * 9000\n$blobC = 'c' * 9000\n"
                        "$blobD = 'd' * 9000\n$blobE = 'e' * 9000\n"},
             target_machine=LOCAL),
        # Each value is capped at 8 KiB, but the envelope cap of 32 KiB is a hard failure
        # rather than a silent truncation - five capped values overshoot it.
        _neg("n3", "return: envelope over 32 KiB", "returnData",
             {"data": {"a": "{{big.param.blobA}}", "b": "{{big.param.blobB}}",
                       "c": "{{big.param.blobC}}", "d": "{{big.param.blobD}}",
                       "e": "{{big.param.blobE}}"}},
             "returnData.envelope.over-limit", "returnData.data",
             "envelope over 32 KiB", ""),
        ret({"contract": "negative", "area": "controlflow"}),
    ]
    return Workflow(
        83, "controlflow", "[TestSuite-Neg] controlflow",
        "An unresolvable child, forEach stopping on its first failing item, the reserved "
        "parameter prefix and a returnData envelope past its hard limit.",
        "negative", "continuous", "B", steps, max_runtime=120)


def hardening_negative():
    """Four guards that appsettings.Development.json deliberately relaxes. On a dev box
    they would not fire at all, so asserting them there would make the suite's verdict
    depend on the host's posture. This workflow ships disabled and is meant for a host
    configured the way production is."""
    steps = [
        janitor(), cid(), mkrun(),
        _neg("n0", "fileOp: path traversal", "fileOperation",
             {"operation": "create",
              "path": RUN_DIR + r"\..\..\..\Windows\Temp\suite-escape.txt"},
             "fileOperation.guard.traversal", "fileOperation.path",
             "traversal rejected", "", LOCAL),
        _neg("n1", "prog: useShellExecute", "startProgram",
             {"filePath": r"C:\Windows\System32\cmd.exe", "arguments": "/c echo x",
              "useShellExecute": True, "waitForExit": True, "timeoutSeconds": 20},
             "startProgram.useShellExecute.blocked", "startProgram.useShellExecute",
             "blocked by StartProgram:DisallowShellExecute", "", LOCAL),
        _neg("n2", "sql: inline connection string", "sql",
             {"provider": "sqlite", "connectionString": "Data Source=:memory:",
              "query": "SELECT 1 AS x", "timeoutSeconds": 10},
             "sql.connectionString.blocked", "sql.connection",
             "raw connection string blocked by RequireConnectionRef", ""),
        Step("cleanup", "Cleanup: run sandbox", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": "$cid = " + CID + "\n"
                        "Remove-Item -LiteralPath (Join-Path '" + RUNS_ROOT + "' $cid) "
                        "-Recurse -Force -ErrorAction SilentlyContinue\n$cleanupDone = 'ok'\n"},
             target_machine=LOCAL),
        ret({"contract": "negative", "area": "hardening"}),
    ]
    return Workflow(
        85, "hardening", "[TestSuite-Neg] production guards",
        "Guards that only exist under production configuration: path traversal, "
        "shell-mediated process start and raw SQL connection strings. Development "
        "relaxes all three, so this workflow ships disabled.",
        "negative", "integration", "C", steps, max_runtime=90,
        requires=["config:FileSystemOperation:RejectTraversal=true",
                  "config:StartProgram:DisallowShellExecute=true",
                  "config:SqlActivity:RequireConnectionRef=true"])


def workflows():
    return [filesystem_negative(), remote_negative(), engine_local_negative(),
            control_flow_negative(), hardening_negative()]
