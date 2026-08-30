"""Shared node builders every suite workflow is assembled from.

Conventions enforced here:
  * `targetMachineId: "localhost"` on every PowerShell-backed node -> in-process bypass,
    no WinRM, no credentials.
  * Per-run state lives under runs/<cid>; the long-lived trigger fixtures under runtime/
    are never touched by a run.
  * Cleanup runs BEFORE the assertion so a red assertion cannot leave residue behind.
    Assertions read the data bus, not the disk, so the order is safe.
"""

from suitelib import Step, RUNTIME_ROOT, RUNS_ROOT, SANDBOX_ROOT, REG_ROOT

LOCAL = "localhost"

# Windows service and scheduled-task fixtures are named from the first 8 characters of the
# correlation id so two runs can never collide on a fixed name.
JANITOR_BASE = r"""
$root = '{root}'
foreach ($d in @('runs', 'runtime\watch', 'runtime\db', 'runtime\acks')) {{
  New-Item -ItemType Directory -Path (Join-Path $root $d) -Force | Out-Null
}}
$cutoff = (Get-Date).AddHours(-1)
Get-ChildItem -LiteralPath (Join-Path $root 'runs') -Directory -ErrorAction SilentlyContinue |
  Where-Object {{ $_.LastWriteTime -lt $cutoff }} |
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$janitorSweep = 'ok'
"""


def sched(cron, label="Schedule"):
    return Step("trg", "%s: %s" % (label, cron), "scheduleTrigger",
                {"cronExpression": cron, "description": "TestSuite cadence"})


def manual(params=None, label="Manual Trigger"):
    return Step("trg", label, "manualTrigger",
                {"title": "Parameter", "parameters": params or []})


def janitor(extra=""):
    """First node of every sandbox-using workflow: creates the fixture roots and evicts
    residue from runs that were cancelled before their own cleanup could run."""
    return Step("janitor", "Janitor: stale run residue", "runScript",
                {"engine": "auto", "timeoutSeconds": 30,
                 "script": JANITOR_BASE.format(root=SANDBOX_ROOT) + extra},
                target_machine=LOCAL)


def cid():
    """Correlation id. Every per-run resource is named from it."""
    return Step("cid", "Correlation id", "generateText", {"mode": "guid"})


CID = "{{cid.param.text}}"
RUN_DIR = RUNS_ROOT + "\\" + CID


def mkrun():
    return Step("mkrun", "Sandbox: create run dir", "folderOperation",
                {"operation": "create", "path": RUN_DIR}, target_machine=LOCAL)


def cleanup(extra=""):
    """Removes this run's sandbox. Deliberately does NOT touch runtime/ - the file
    watcher directory and the sentinel database live there and must survive."""
    script = """
$cid = {cid}
$dir = Join-Path '{runs}' $cid
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
{extra}
$cleanupDone = 'ok'
""".format(cid=CID, runs=RUNS_ROOT, extra=extra)
    return Step("cleanup", "Cleanup: run sandbox", "runScript",
                {"engine": "auto", "timeoutSeconds": 30, "script": script},
                target_machine=LOCAL)


def assert_step(body, label="Assert: all variants"):
    """A throw here fails the step, and any failed step makes the whole execution Failed
    (WorkflowEngine reads the terminal verdict from the failed-step count). That is the
    entire regression signal for the positive contract."""
    return Step("assert", label, "runScript",
                {"engine": "auto", "timeoutSeconds": 60, "script": body},
                target_machine=LOCAL)


def ret(data):
    return Step("ret", "Return", "returnData", {"data": data})


def ok_return(activity, with_cid=False):
    """Terminal node of a positive workflow. `cid` is only echoed back when the workflow
    actually has a correlation-id node - a template pointing at a step that does not
    exist fails the step outright."""
    data = {"activity": activity, "asserted": "{{assert.success}}"}
    if with_cid:
        data["cid"] = CID
    return ret(data)
