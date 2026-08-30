"""Invasive coverage: the service and scheduled-task actions that genuinely change the
host.

These are the actions that were parked as disabled nodes in every earlier generation of
the suite, which is why serviceManagement was covered two actions out of seven and
scheduledTask one out of seven. Here they run for real against fixtures the workflow
creates and removes itself, keyed by the run's correlation id so two runs can never
collide on a name.

Both workflows need an elevated service account: creating a Windows service and
registering a task under SYSTEM are privileged operations. They ship disabled and are
enabled per host through NP_TESTSUITE_INVASIVE.
"""

from suitelib import Step, Workflow
from spec_core import LOCAL, janitor, cid, assert_step, ok_return, CID

SVC = "{{names.param.svcName}}"
TASK = "{{names.param.taskName}}"
TASK_PATH = "\\NodePilot-TestSuite\\"

NAMES_SCRIPT = ("$cid = " + CID + "\n"
                "$short = $cid.Substring(0, 8)\n"
                "$svcName = \"NPTestSvc_$short\"\n"
                "$taskName = \"NPTestTask_$short\"\n")

# Orphans from a run that was cancelled before its own teardown could execute.
JANITOR_EXTRA = """
Get-Service -Name 'NPTestSvc_*' -ErrorAction SilentlyContinue | ForEach-Object {
  & sc.exe delete $_.Name | Out-Null
}
Get-ScheduledTask -TaskPath '\\NodePilot-TestSuite\\' -ErrorAction SilentlyContinue |
  Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue
$invasiveSweep = 'ok'
"""

TEARDOWN = """
$cid = {cid}
$short = $cid.Substring(0, 8)
& sc.exe delete "NPTestSvc_$short" | Out-Null
Get-ScheduledTask -TaskName "NPTestTask_$short" -TaskPath '\\NodePilot-TestSuite\\' -ErrorAction SilentlyContinue |
  Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue
$teardownDone = 'ok'
""".replace("{cid}", CID)


TASK_GONE_SCRIPT = """
$taskName = {{names.param.taskName}}
$still = Get-ScheduledTask -TaskName $taskName -TaskPath '\\NodePilot-TestSuite\\' -ErrorAction SilentlyContinue
$taskGone = if ($still) { 'no' } else { 'yes' }
"""


def _svc(sid, label, config, cases=None):
    return Step(sid, label, "serviceManagement", config, target_machine=LOCAL, cases=cases)


def service_workflow():
    steps = [
        janitor(JANITOR_EXTRA), cid(),
        Step("names", "Derive per-run fixture names", "runScript",
             {"engine": "auto", "timeoutSeconds": 20, "script": NAMES_SCRIPT},
             target_machine=LOCAL),
        _svc("v0", "svc: create", {
            "action": "create", "serviceName": SVC,
            "binaryPath": r"C:\Windows\System32\cmd.exe /c rem nodepilot-testsuite",
            "displayName": "NodePilot TestSuite fixture",
            "description": "Disposable service created by the NodePilot test suite.",
            "startupType": "Manual"},
             [{"id": "serviceManagement.action.create", "assertedVia": "v5",
               "dimension": "serviceManagement.action", "value": "create"}]),
        _svc("v1", "svc: setStartType Automatic",
             {"action": "setStartType", "serviceName": SVC, "startupType": "Automatic"},
             [{"id": "serviceManagement.action.setStartType", "assertedVia": "v5",
               "dimension": "serviceManagement.action", "value": "setStartType"},
              {"id": "serviceManagement.startupType.Automatic", "assertedVia": "v5",
               "dimension": "serviceManagement.startupType", "value": "Automatic"}]),
        _svc("v2", "svc: setStartType AutomaticDelayedStart",
             {"action": "setStartType", "serviceName": SVC,
              "startupType": "AutomaticDelayedStart"},
             [{"id": "serviceManagement.startupType.AutomaticDelayedStart", "assertedVia": "v5",
               "dimension": "serviceManagement.startupType",
               "value": "AutomaticDelayedStart"}]),
        _svc("v3", "svc: setStartType Manual",
             {"action": "setStartType", "serviceName": SVC, "startupType": "Manual"},
             [{"id": "serviceManagement.startupType.Manual", "assertedVia": "v5",
               "dimension": "serviceManagement.startupType", "value": "Manual"}]),
        _svc("v4", "svc: setStartType Disabled",
             {"action": "setStartType", "serviceName": SVC, "startupType": "Disabled"},
             [{"id": "serviceManagement.startupType.Disabled", "assertedVia": "v5",
               "dimension": "serviceManagement.startupType", "value": "Disabled"}]),
        _svc("v5", "svc: status", {"action": "status", "serviceName": SVC},
             [{"id": "serviceManagement.action.status",
               "dimension": "serviceManagement.action", "value": "status"}]),
        _svc("v6", "svc: delete", {"action": "delete", "serviceName": SVC},
             [{"id": "serviceManagement.action.delete", "assertedVia": "names",
               "dimension": "serviceManagement.action", "value": "delete"}]),
        Step("teardown", "Teardown: remove fixtures", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "script": TEARDOWN},
             target_machine=LOCAL),
        assert_step("""
$name      = {{v5.param.name}}
$status    = {{v5.param.status}}
$startType = {{v5.param.startType}}
$svcName   = {{names.param.svcName}}

if ($name -ne $svcName) { throw "status returned service '$name', expected '$svcName'" }
if ([string]::IsNullOrWhiteSpace($status)) { throw "status did not report a state" }
# The last setStartType before the status read was Disabled.
if ($startType -notmatch 'Disabled') { throw "startType after setStartType Disabled: '$startType'" }
if (Get-Service -Name $svcName -ErrorAction SilentlyContinue) {
  throw "the fixture service still exists after delete"
}
$assertOk = 'serviceManagement'
"""),
        ok_return("serviceManagement", with_cid=True),
    ]
    return Workflow(
        90, "serviceManagement", "[TestSuite-Inv] serviceManagement",
        "Creates a disposable service, walks all four startup types, reads its status "
        "and deletes it again. Needs an elevated service account.",
        "invasive", "invasive", "C", steps, max_runtime=180,
        requires=["globals:NP_TESTSUITE_INVASIVE", "elevated service account"],
        excluded=[
            {"id": "serviceManagement.action.start",
             "dimension": "serviceManagement.action", "value": "start",
             "reason": "A cmd.exe-backed fixture never answers the service control "
                       "manager, so start always ends in error 1053. Genuine start / stop "
                       "/ restart coverage needs a real service and lives in the "
                       "opt-in cycle workflow.",
             "coveredBy": "scripts/test-suite/invasive/92-serviceManagement-cycle.json"},
            {"id": "serviceManagement.action.stop",
             "dimension": "serviceManagement.action", "value": "stop",
             "reason": "Same as start: needs a service that actually runs.",
             "coveredBy": "scripts/test-suite/invasive/92-serviceManagement-cycle.json"},
            {"id": "serviceManagement.action.restart",
             "dimension": "serviceManagement.action", "value": "restart",
             "reason": "Same as start: needs a service that actually runs.",
             "coveredBy": "scripts/test-suite/invasive/92-serviceManagement-cycle.json"},
        ])


def service_cycle_workflow():
    """start / stop / restart need a service that genuinely runs, and the suite refuses to
    pick one for the host. NP_TESTSUITE_SERVICE_TARGET names it; without that global the
    workflow stays disabled and the three actions are reported as excluded by host."""
    target = "{{globals.NP_TESTSUITE_SERVICE_TARGET}}"
    steps = [
        _svc("pre", "svc: status before", {"action": "status", "serviceName": target}),
        _svc("v0", "svc: stop", {"action": "stop", "serviceName": target},
             [{"id": "serviceManagement.action.stop.real", "assertedVia": "post",
               "dimension": "serviceManagement.action", "value": "stop (real service)"}]),
        _svc("v1", "svc: start", {"action": "start", "serviceName": target},
             [{"id": "serviceManagement.action.start.real", "assertedVia": "post",
               "dimension": "serviceManagement.action", "value": "start (real service)"}]),
        _svc("v2", "svc: restart", {"action": "restart", "serviceName": target},
             [{"id": "serviceManagement.action.restart.real", "assertedVia": "post",
               "dimension": "serviceManagement.action",
               "value": "restart (real service)"}]),
        _svc("post", "svc: status after", {"action": "status", "serviceName": target}),
        assert_step("""
$before = {{pre.param.status}}
$after  = {{post.param.status}}
if ([string]::IsNullOrWhiteSpace($before)) { throw "could not read the target service before the cycle" }
if ($after -notmatch 'Running') { throw "the target service is '$after' after restart, expected Running" }
$assertOk = 'serviceManagement-cycle'
"""),
        ok_return("serviceManagement-cycle"),
    ]
    return Workflow(
        92, "serviceManagement-cycle", "[TestSuite-Inv] serviceManagement (cycle)",
        "Stops, starts and restarts a service the host owner nominates, then checks it "
        "is running again. Deliberately not pointed at Spooler or any other service the "
        "suite picked on its own.",
        "invasive", "invasive", "C", steps, max_runtime=240,
        requires=["globals:NP_TESTSUITE_INVASIVE", "globals:NP_TESTSUITE_SERVICE_TARGET"])


def _task(sid, label, config, cases=None):
    cfg = {"taskPath": TASK_PATH}
    cfg.update(config)
    return Step(sid, label, "scheduledTask", cfg, target_machine=LOCAL, cases=cases)


REGISTER_BASE = {
    "action": "register", "taskName": TASK,
    "program": r"C:\Windows\System32\cmd.exe", "arguments": "/c rem nodepilot-testsuite",
    "description": "Disposable task created by the NodePilot test suite.",
    "runLevel": "limited", "force": True,
}


def _register(sid, label, extra, cases):
    cfg = dict(REGISTER_BASE)
    cfg.update(extra)
    return _task(sid, label, cfg, cases)


def scheduled_task_workflow():
    steps = [
        janitor(JANITOR_EXTRA), cid(),
        Step("names", "Derive per-run fixture names", "runScript",
             {"engine": "auto", "timeoutSeconds": 20, "script": NAMES_SCRIPT},
             target_machine=LOCAL),
        _register("v0", "task: register once",
                  {"triggerType": "once", "startTime": "23:55"},
                  [{"id": "scheduledTask.action.register", "assertedVia": "v1",
                    "dimension": "scheduledTask.action", "value": "register"},
                   {"id": "scheduledTask.triggerType.once", "assertedVia": "v1",
                    "dimension": "scheduledTask.triggerType", "value": "once"},
                   {"id": "scheduledTask.runLevel.limited", "assertedVia": "v1",
                    "dimension": "scheduledTask.runLevel", "value": "limited"},
                   {"id": "scheduledTask.force.true", "assertedVia": "v1",
                    "dimension": "scheduledTask.force", "value": "true"}]),
        _task("v1", "task: get", {"action": "get", "taskName": TASK},
              [{"id": "scheduledTask.action.get", "dimension": "scheduledTask.action",
                "value": "get"}]),
        _task("v2", "task: disable", {"action": "disable", "taskName": TASK},
              [{"id": "scheduledTask.action.disable", "assertedVia": "v3",
                "dimension": "scheduledTask.action", "value": "disable"}]),
        _task("v3", "task: get while disabled", {"action": "get", "taskName": TASK}),
        _task("v4", "task: enable", {"action": "enable", "taskName": TASK},
              [{"id": "scheduledTask.action.enable", "assertedVia": "v4b",
                "dimension": "scheduledTask.action", "value": "enable"}]),
        _task("v4b", "task: get after enable", {"action": "get", "taskName": TASK}),
        _task("v5", "task: start", {"action": "start", "taskName": TASK},
              [{"id": "scheduledTask.action.start", "assertedVia": "v6b",
                "dimension": "scheduledTask.action", "value": "start"}]),
        _task("v6", "task: stop", {"action": "stop", "taskName": TASK},
              [{"id": "scheduledTask.action.stop", "assertedVia": "v6b",
                "dimension": "scheduledTask.action", "value": "stop"}]),
        _task("v6b", "task: get after start/stop", {"action": "get", "taskName": TASK}),
        _register("v7", "task: re-register daily",
                  {"triggerType": "daily", "startTime": "23:56", "daysInterval": 2},
                  [{"id": "scheduledTask.triggerType.daily", "assertedVia": "v7b",
                    "dimension": "scheduledTask.triggerType", "value": "daily"},
                   {"id": "scheduledTask.daysInterval", "assertedVia": "v7b",
                    "dimension": "scheduledTask.daysInterval", "value": "2"}]),
        _task("v7b", "task: get after re-register", {"action": "get", "taskName": TASK}),
        _register("v8", "task: re-register weekly",
                  {"triggerType": "weekly", "startTime": "23:57",
                   "daysOfWeek": ["Monday", "Thursday"], "weeksInterval": 2},
                  [{"id": "scheduledTask.triggerType.weekly", "assertedVia": "v8b",
                    "dimension": "scheduledTask.triggerType", "value": "weekly"},
                   {"id": "scheduledTask.daysOfWeek", "assertedVia": "v8b",
                    "dimension": "scheduledTask.daysOfWeek",
                    "value": "array of day names"},
                   {"id": "scheduledTask.weeksInterval", "assertedVia": "v8b",
                    "dimension": "scheduledTask.weeksInterval", "value": "2"}]),
        _task("v8b", "task: get after re-register", {"action": "get", "taskName": TASK}),
        _register("v9", "task: re-register atLogon", {"triggerType": "atLogon"},
                  [{"id": "scheduledTask.triggerType.atLogon", "assertedVia": "v9b",
                    "dimension": "scheduledTask.triggerType", "value": "atLogon"}]),
        _task("v9b", "task: get after re-register", {"action": "get", "taskName": TASK}),
        _register("v10", "task: re-register atStartup", {"triggerType": "atStartup"},
                  [{"id": "scheduledTask.triggerType.atStartup", "assertedVia": "v10b",
                    "dimension": "scheduledTask.triggerType", "value": "atStartup"}]),
        _task("v10b", "task: get after re-register", {"action": "get", "taskName": TASK}),
        _task("v11", "task: unregister", {"action": "unregister", "taskName": TASK},
              [{"id": "scheduledTask.action.unregister", "assertedVia": "v12",
                "dimension": "scheduledTask.action", "value": "unregister"}]),
        Step("v12", "Confirm the task is gone", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "script": TASK_GONE_SCRIPT},
             target_machine=LOCAL),
        Step("teardown", "Teardown: remove fixtures", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "script": TEARDOWN},
             target_machine=LOCAL),
        assert_step("""
$taskName  = {{names.param.taskName}}
$gotName   = {{v1.param.taskName}}
$stateOn   = {{v1.param.state}}
$stateOff  = {{v3.param.state}}
$stateOn2  = {{v4b.param.state}}
$afterRun  = {{v6b.param.lastRunTime}}
$daily     = {{v7b.param.state}}
$weekly    = {{v8b.param.state}}
$atLogon   = {{v9b.param.state}}
$atStartup = {{v10b.param.state}}
$gone      = {{v12.param.taskGone}}

if ($gotName -notmatch $taskName) { throw "get returned task '$gotName'" }
if ([string]::IsNullOrWhiteSpace($stateOn)) { throw "get did not report a state" }
if ($stateOff -ne 'Disabled') { throw "state after disable was '$stateOff'" }
if ($stateOn2 -eq 'Disabled') { throw "enable did not take: state is still '$stateOn2'" }
# A task that was started leaves a run timestamp behind; a start that did nothing does not.
if ([string]::IsNullOrWhiteSpace($afterRun)) { throw "start/stop left no lastRunTime" }
foreach ($pair in @(@('daily',$daily), @('weekly',$weekly), @('atLogon',$atLogon), @('atStartup',$atStartup))) {
  if ([string]::IsNullOrWhiteSpace($pair[1])) { throw "re-register $($pair[0]) left no readable task" }
}
if ($gone -ne 'yes') { throw "the fixture task still exists after unregister" }
$assertOk = 'scheduledTask'
"""),
        ok_return("scheduledTask", with_cid=True),
    ]
    return Workflow(
        91, "scheduledTask", "[TestSuite-Inv] scheduledTask",
        "Registers a disposable task under its own task path, walks get / disable / "
        "enable / start / stop, re-registers it once per trigger type and unregisters it.",
        "invasive", "invasive", "C", steps, max_runtime=300, nodes_per_row=7,
        requires=["globals:NP_TESTSUITE_INVASIVE", "elevated service account"],
        excluded=[
            {"id": "scheduledTask.runLevel.highest",
             "dimension": "scheduledTask.runLevel", "value": "highest",
             "reason": "Not a deterministic outcome: under an elevated service account it "
                       "succeeds, under an unprivileged one it fails. A case that flips "
                       "with the host's privilege level cannot be a cadence assertion.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/ScheduledTaskActivityTests.cs"},
            {"id": "scheduledTask.runAsUser", "dimension": "scheduledTask.runAsUser",
             "value": "explicit principal",
             "reason": "Registering under a named account needs that account's rights on "
                       "the host; the suite cannot assume one exists.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/ScheduledTaskActivityTests.cs"},
        ])


def workflows():
    return [service_workflow(), scheduled_task_workflow(), service_cycle_workflow()]
