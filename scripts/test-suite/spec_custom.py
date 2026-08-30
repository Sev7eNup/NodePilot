"""Custom-activity coverage.

A `custom:<key>` node is a runScript preset behind a single sentinel-registered executor.
Two things make it worth its own workflow rather than folding it into the runScript one:
the node carries a reference to a database-backed definition (`__customDefinitionId`, with
`__customKey` as a drift cross-check), and the executor captures ONLY the outputs the
definition declares - a script variable that is not declared is dropped.

The definition itself is not a workflow, so it cannot live in an export envelope. It ships
as `custom-activity-definition.json` and Install-TestSuite.ps1 creates it, enables it as
Admin, and substitutes the resulting id into the placeholder below. Imported definitions
land disabled, and a node pointing at a disabled definition fails outright, so the order
matters.
"""

from suitelib import Step, Workflow
from spec_core import assert_step, ok_return

KEY = "testsuite-probe"
DEFINITION_ID = "__TESTSUITE_CUSTOM_DEFINITION_ID__"

# Mirrors the definition in custom-activity-definition.json. Declared inputs become config
# keys; declared outputs are the allow-list the wrapper captures.
DEFINITION = {
    "key": KEY,
    "name": "TestSuite Probe",
    "description": "Disposable custom activity the NodePilot test suite exercises.",
    "icon": "beaker",
    "color": None,
    "engine": "auto",
    "runsRemote": False,
    "isolated": False,
    "memoryLimitMb": None,
    "maxProcesses": None,
    "defaultTimeoutSeconds": 30,
    "successExitCodes": None,
    # Declared inputs arrive as PowerShell variables ($label, $repeat, $loud) that the
    # wrapper injects - they are not {{name}} placeholders in the template.
    "scriptTemplate": (
        "$repeatCount = [int]$repeat\n"
        "$echoed = ($label * $repeatCount)\n"
        "if ($loud -eq 'true') { $echoed = $echoed.ToUpperInvariant() }\n"
        "$charCount = $echoed.Length.ToString()\n"
        "$undeclared = 'this must not be captured'\n"
        "Write-Output $echoed\n"
    ),
    "inputs": [
        {"name": "label", "label": "Label", "type": "string", "required": True,
         "default": "np"},
        {"name": "repeat", "label": "Repeat", "type": "number", "required": False,
         "default": "2"},
        {"name": "loud", "label": "Upper case", "type": "boolean", "required": False,
         "default": "false"},
    ],
    "outputs": [
        {"name": "echoed", "type": "string"},
        {"name": "charCount", "type": "number"},
    ],
}


def _custom(sid, label, config, cases=None):
    cfg = {"__customDefinitionId": DEFINITION_ID, "__customKey": KEY}
    cfg.update(config)
    return Step(sid, label, "custom:" + KEY, cfg, cases=cases)


def custom_activity_workflow():
    steps = [
        _custom("v0", "custom: declared inputs",
                {"label": "ab", "repeat": "3", "loud": "false"},
                [{"id": "custom.dispatch", "dimension": "custom.activityType",
                  "value": "custom:<key> routed to the sentinel executor"},
                 {"id": "custom.inputs.declared", "dimension": "custom.inputs",
                  "value": "declared inputs become config keys"},
                 {"id": "custom.outputs.declared", "dimension": "custom.outputs",
                  "value": "declared outputs captured as param.*"}]),
        _custom("v1", "custom: boolean input",
                {"label": "xy", "repeat": "2", "loud": "true"},
                [{"id": "custom.inputs.boolean", "dimension": "custom.inputs",
                  "value": "boolean input"}]),
        _custom("v2", "custom: input defaults", {},
                [{"id": "custom.inputs.default", "dimension": "custom.inputs",
                  "value": "omitted input falls back to its declared default"}]),
        _custom("v3", "custom: per-node timeout",
                {"label": "t", "repeat": "1", "timeoutSeconds": 45},
                [{"id": "custom.timeoutSeconds", "dimension": "custom.timeoutSeconds",
                  "value": "per-node override of the definition default"}]),
        assert_step("""
$echoed   = {{v0.param.echoed}}
$chars    = {{v0.param.charCount}}
$exitCode = {{v0.param.exitCode}}
$loud     = {{v1.param.echoed}}
$defaults = {{v2.param.echoed}}
$timed    = {{v3.param.echoed}}
$leak     = "{{v0.param.undeclared}}"

if ($echoed -ne 'ababab') { throw "custom activity echoed '$echoed', expected 'ababab'" }
if ([int]$chars -ne 6) { throw "declared numeric output charCount was '$chars'" }
# The script runs no native command, so its exit code is 0 — regardless of what any earlier
# workflow left in the pooled runspace. This is the cadence guard for the $LASTEXITCODE reset.
if ($exitCode -ne '0') { throw "exitCode leaked from an earlier script: got '$exitCode'" }
if ($loud -ne 'XYXY') { throw "boolean input did not take: '$loud'" }
# label and repeat were omitted, so both come from the definition's declared defaults.
if ($defaults -ne 'npnp') { throw "input defaults resolved to '$defaults'" }
if ($timed -ne 't') { throw "per-node timeout variant produced '$timed'" }
# Output capture is allow-listed: a script variable the definition does not declare must
# not reach the data bus, so this template stays an unresolved literal.
if ($leak -notmatch 'undeclared') { throw "an undeclared script variable was captured: '$leak'" }
$assertOk = 'custom-activity'
"""),
        ok_return("custom-activity"),
    ]
    return Workflow(
        36, "custom-activity", "[TestSuite] custom activity",
        "Dispatch through the sentinel executor, declared inputs and their defaults, the "
        "declared-output allow-list and a per-node timeout override.",
        "positive", "continuous", "A", steps, max_runtime=90,
        requires=["custom activity '" + KEY + "' created and enabled by the installer"],
        excluded=[
            {"id": "custom.runsRemote", "dimension": "custom.runsRemote", "value": "true",
             "reason": "RunsRemote comes from the definition, not the node, and a remote "
                       "definition needs a registered WinRM target the suite does not have.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/CustomActivityTests.cs"},
            {"id": "custom.isolated", "dimension": "custom.isolated", "value": "true",
             "reason": "Also a definition-level flag; process isolation itself is already "
                       "exercised on the runScript path it shares.",
             "coveredBy": "scripts/test-suite/positive/15-runScript.json"},
        ])


def workflows():
    return [custom_activity_workflow()]
