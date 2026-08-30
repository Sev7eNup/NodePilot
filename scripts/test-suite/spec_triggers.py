"""Trigger coverage: the six trigger types, driven for real.

No earlier generation of the suite ever made a trigger fire. The five passive types have
no cadence of their own, and they cannot be judged by starting them by hand either - a
manually started run has none of the trigger parameters the workflow is there to prove.

So the loop is closed with correlation ids and acknowledgement files instead of an
authenticated call back into the API (a restApi step has no durable session, and an admin
token has no business sitting in workflow JSON):

  driver  -> writes a file / bumps a sentinel / posts a webhook, each carrying one cid
  trigger -> fires, reads the cid out of its own trigger parameters, writes runtime/acks/<type>/<cid>
  driver  -> waits for the three ack files and reports what is missing

The fixtures the sources watch live under runtime/ and are deliberately outside the
per-run sandbox: deleting them would kill the file watcher's directory and the sentinel
database underneath a running source.
"""

from suitelib import Step, Workflow, RUNTIME_ROOT
from spec_core import LOCAL, manual, assert_step, ok_return, ret

WATCH_DIR = RUNTIME_ROOT + r"\watch"
ACK_DIR = RUNTIME_ROOT + r"\acks"
SENTINEL_DB = RUNTIME_ROOT + r"\db\sentinel.sqlite"
WEBHOOK_URL = "{{globals.NP_TESTSUITE_WEBHOOK_URL}}"
WEBHOOK_SECRET = "__TESTSUITE_WEBHOOK_SECRET__"
EVENT_SOURCE = "NodePilot-TestSuite"


def _ack(kind, cid_expression, extra=""):
    """Writes runtime/acks/<kind>/<cid>. The cid expression differs per trigger because
    each source hands its payload over in a different shape."""
    script = (
        "$cid = " + cid_expression + "\n"
        + extra +
        "if ([string]::IsNullOrWhiteSpace($cid)) { throw 'trigger fired without a correlation id' }\n"
        "$ackDir = Join-Path '" + ACK_DIR + "' '" + kind + "'\n"
        "New-Item -ItemType Directory -Path $ackDir -Force | Out-Null\n"
        "Set-Content -LiteralPath (Join-Path $ackDir $cid) -Value (Get-Date -Format o)\n"
        "$ackedCid = $cid\n")
    return Step("ack", "Acknowledge: " + kind, "runScript",
                {"engine": "auto", "timeoutSeconds": 30, "script": script},
                target_machine=LOCAL)


def schedule_trigger_workflow():
    steps = [
        Step("read", "Read the trigger's own parameters", "log",
             {"level": "info",
              "message": "fired={{trg.param.firedAt}} next={{trg.param.nextFireAt}}"},
             cases=[{"id": "scheduleTrigger.cronExpression",
                     "dimension": "scheduleTrigger.cronExpression",
                     "value": "7-field Quartz cron"},
                    {"id": "scheduleTrigger.output.firedAt",
                     "dimension": "scheduleTrigger.output",
                     "value": "firedAt / nextFireAt"}]),
        assert_step("""
$msg = {{read.param.message}}
# These parameters only exist on a run the scheduler started; a manual run has none,
# which is exactly why this workflow is judged from its cadence and not from -Once.
if ($msg -match 'fired=\\s*next=') { throw "scheduleTrigger emitted no firedAt: '$msg'" }
if ($msg -match '\\{\\{') { throw "trigger parameters left unresolved: '$msg'" }
$assertOk = 'scheduleTrigger'
"""),
        ok_return("scheduleTrigger"),
    ]
    return Workflow(
        50, "trigger-schedule", "[TestSuite] trigger: schedule",
        "The cadence itself is the test. Asserts the trigger hands firedAt and "
        "nextFireAt to the run, which only a scheduler-started execution carries.",
        "positive", "continuous", "A", steps, max_runtime=45, judge_by="cadence")


def manual_trigger_workflow():
    steps = [
        Step("read", "Both notations of a trigger input", "log",
             {"level": "info",
              "message": "manual={{manual.probe}} param={{trg.param.probe}} "
                         "defaulted={{manual.withDefault}}"},
             cases=[{"id": "manualTrigger.parameters.declared",
                     "dimension": "manualTrigger.parameters",
                     "value": "declared input"},
                    {"id": "manualTrigger.parameters.default",
                     "dimension": "manualTrigger.parameters",
                     "value": "default seeded when the caller omits it"},
                    {"id": "variable.namespace.manual",
                     "dimension": "variable.namespace",
                     "value": "{{manual.NAME}} and {{trg.param.NAME}} are the same value"}]),
        assert_step("""
$msg = {{read.param.message}}
if ($msg -match '\\{\\{') { throw "trigger input left unresolved: '$msg'" }
if ($msg -notmatch 'manual=(\\S+) param=\\1 ') {
  throw "manual.NAME and trg.param.NAME disagree: '$msg'"
}
# A declared parameter with a default is seeded even when the caller leaves it out.
if ($msg -notmatch 'defaulted=seeded-default') { throw "declared default was not seeded: '$msg'" }
$assertOk = 'manualTrigger'
"""),
        ok_return("manualTrigger"),
    ]
    return Workflow(
        51, "trigger-manual", "[TestSuite] trigger: manual",
        "Both notations of a trigger input resolve to the same value, and a declared "
        "parameter the caller omits is seeded from its default.",
        "positive", "continuous", None, steps, max_runtime=45, judge_by="cadence",
        trigger=manual([
            {"name": "probe", "type": "string", "required": False, "default": ""},
            {"name": "withDefault", "type": "string", "required": False,
             "default": "seeded-default"},
        ]))


def webhook_trigger_workflow():
    steps = [
        _ack("webhook", "{{manual.cid}}"),
        Step("read", "Read the webhook payload", "log",
             {"level": "info",
              "message": "method={{trg.param.webhookMethod}} path={{trg.param.webhookPath}} "
                         "cid={{manual.cid}}"},
             cases=[{"id": "webhookTrigger.signatureMode.header",
                     "dimension": "webhookTrigger.signatureMode",
                     "value": "header (X-Webhook-Secret)"},
                    {"id": "webhookTrigger.fieldMappings",
                     "dimension": "webhookTrigger.fieldMappings",
                     "value": "JSONPath extraction into manual.*"},
                    {"id": "webhookTrigger.output.body",
                     "dimension": "webhookTrigger.output",
                     "value": "webhookBody / webhookMethod / webhookPath"}]),
        assert_step("""
$msg  = {{read.param.message}}
$body = {{trg.param.webhookBody}}
$acked = {{ack.param.ackedCid}}
if ($msg -match '\{\{') { throw "webhook parameters left unresolved: '$msg'" }
if ($msg -notmatch 'method=POST') { throw "webhookMethod was not POST: '$msg'" }
if ($msg -notmatch 'path=suite') { throw "webhookPath: '$msg'" }
if ($body -notmatch 'cid') { throw "webhookBody did not carry the payload: '$body'" }
if ([string]::IsNullOrWhiteSpace($acked)) { throw "no correlation id was acknowledged" }
$assertOk = 'webhookTrigger'
"""),
        ret({"trigger": "webhook", "cid": "{{manual.cid}}"}),
    ]
    return Workflow(
        52, "trigger-webhook", "[TestSuite] trigger: webhook",
        "Fired by the driver over HTTP with a shared secret; the correlation id arrives "
        "through a fieldMappings JSONPath extraction.",
        "positive", "continuous", None, steps, max_runtime=45, judge_by="cadence",
        requires=["globals:NP_TESTSUITE_WEBHOOK_URL", "globals:NP_TESTSUITE_WEBHOOK_SECRET"],
        trigger=Step("trg", "Webhook", "webhookTrigger",
                     {"path": "suite", "method": "POST",
                      "secret": WEBHOOK_SECRET, "signatureMode": "header",
                      "fieldMappings": [{"name": "cid", "path": "$.cid"}]}),
        excluded=[
            {"id": "webhookTrigger.signatureMode.hmac-v2",
             "dimension": "webhookTrigger.signatureMode", "value": "nodepilot-hmac-v2",
             "reason": "Needs a CSPRNG secret of at least 32 bytes plus a freshness "
                       "timestamp and a unique delivery id computed per call. The driver "
                       "covers the signing path; a second always-on webhook endpoint "
                       "would add a second public surface for no extra coverage.",
             "coveredBy": "tests/NodePilot.Api.Tests"},
        ])


def file_watcher_trigger_workflow():
    steps = [
        _ack("filewatcher", "{{trg.param.fileNameWithoutExtension}}",
             extra="$action = {{trg.param.fileAction}}\n"
                   "if ($action -ne 'Created') { throw \"unexpected fileAction '$action'\" }\n"),
        Step("read", "Read the file event", "log",
             {"level": "info",
              "message": "action={{trg.param.fileAction}} name={{trg.param.fileName}} "
                         "dir={{trg.param.fileDirectory}}"},
             cases=[{"id": "fileWatcherTrigger.watchType.created",
                     "dimension": "fileWatcherTrigger.watchType", "value": "Created"},
                    {"id": "fileWatcherTrigger.filter",
                     "dimension": "fileWatcherTrigger.filter", "value": "*.txt"},
                    {"id": "fileWatcherTrigger.output.file",
                     "dimension": "fileWatcherTrigger.output",
                     "value": "fileAction / filePath / fileName"}]),
        assert_step("""
$msg   = {{read.param.message}}
$acked = {{ack.param.ackedCid}}
if ($msg -match '\{\{') { throw "file watcher parameters left unresolved: '$msg'" }
if ($msg -notmatch 'action=Created') { throw "fileAction: '$msg'" }
if ($msg -notmatch '\.txt') { throw "the filter should only admit .txt files: '$msg'" }
if ([string]::IsNullOrWhiteSpace($acked)) { throw "no correlation id was acknowledged" }
$assertOk = 'fileWatcherTrigger'
"""),
        ret({"trigger": "filewatcher", "cid": "{{trg.param.fileNameWithoutExtension}}"}),
    ]
    return Workflow(
        53, "trigger-filewatcher", "[TestSuite] trigger: file watcher",
        "Watches the long-lived runtime/watch fixture. The driver drops one file per "
        "cadence whose name is the correlation id.",
        "positive", "continuous", None, steps, max_runtime=45, judge_by="cadence",
        trigger=Step("trg", "File Watcher", "fileWatcherTrigger",
                     {"directory": WATCH_DIR, "filter": "*.txt",
                      "watchType": "Created", "includeSubdirectories": False}),
        excluded=[
            {"id": "fileWatcherTrigger.watchType.other",
             "dimension": "fileWatcherTrigger.watchType",
             "value": "changed / deleted / renamed / any",
             "reason": "One directory can only carry one watch configuration, and four "
                       "more always-on watchers plus their driver legs would quadruple "
                       "the fixture surface for one enum.",
             "coveredBy": "tests/NodePilot.Engine.Tests"},
            {"id": "fileWatcherTrigger.includeSubdirectories",
             "dimension": "fileWatcherTrigger.includeSubdirectories", "value": "true",
             "reason": "Same reason: it is a second watcher on the same fixture.",
             "coveredBy": "tests/NodePilot.Engine.Tests"},
        ])


def database_trigger_workflow():
    steps = [
        # The source only forwards the sentinel value, so the correlation id has to BE the
        # sentinel - there is no second channel to carry it on.
        _ack("database", "{{trg.param.dbSentinel}}"),
        Step("read", "Read the sentinel change", "log",
             {"level": "info",
              "message": "now={{trg.param.dbSentinel}} before={{trg.param.dbPrevious}}"},
             cases=[{"id": "databaseTrigger.provider.sqlite",
                     "dimension": "databaseTrigger.provider", "value": "sqlite"},
                    {"id": "databaseTrigger.pollingIntervalSeconds",
                     "dimension": "databaseTrigger.pollingIntervalSeconds", "value": "10"},
                    {"id": "databaseTrigger.output.sentinel",
                     "dimension": "databaseTrigger.output",
                     "value": "dbSentinel / dbPrevious"}]),
        assert_step("""
$msg   = {{read.param.message}}
$now   = {{trg.param.dbSentinel}}
$prev  = {{trg.param.dbPrevious}}
$acked = {{ack.param.ackedCid}}
if ($msg -match '\{\{') { throw "database trigger parameters left unresolved: '$msg'" }
if ([string]::IsNullOrWhiteSpace($now)) { throw "dbSentinel was empty" }
if ($now -eq $prev) { throw "the source fired without the sentinel having moved" }
if ([string]::IsNullOrWhiteSpace($acked)) { throw "no correlation id was acknowledged" }
$assertOk = 'databaseTrigger'
"""),
        ret({"trigger": "database", "cid": "{{trg.param.dbSentinel}}"}),
    ]
    return Workflow(
        54, "trigger-database", "[TestSuite] trigger: database",
        "Polls the long-lived sentinel database. The driver writes the correlation id "
        "into the sentinel itself, because that value is all the source forwards.",
        "positive", "continuous", None, steps, max_runtime=45, judge_by="cadence",
        requires=["config:Trigger:Database:RequireConnectionRef=false, or a connectionRef "
                  "pointing at runtime/db/sentinel.sqlite"],
        trigger=Step("trg", "DB Poll", "databaseTrigger",
                     {"provider": "sqlite",
                      "connectionString": "Data Source=" + SENTINEL_DB,
                      "query": "SELECT sentinel FROM suite_sentinel WHERE id = 1",
                      "pollingIntervalSeconds": 10}),
        excluded=[
            {"id": "databaseTrigger.provider.sqlserver",
             "dimension": "databaseTrigger.provider", "value": "sqlserver",
             "reason": "The source supports only sqlserver and sqlite, and no SQL Server "
                       "instance is part of the development environment.",
             "coveredBy": "tests/NodePilot.Engine.Tests"},
            {"id": "databaseTrigger.connectionRef",
             "dimension": "databaseTrigger.connectionRef", "value": "named connection",
             "reason": "Needs Trigger:Database:Connections:<name> in the host's "
                       "configuration; the suite ships with the inline form that "
                       "development permits.",
             "coveredBy": "tests/NodePilot.Engine.Tests"},
        ])


def event_log_trigger_workflow():
    steps = [
        _ack("eventlog", "$matched",
             extra="$message = {{trg.param.eventMessage}}\n"
                   "$m = [regex]::Match($message, 'cid=([0-9a-fA-F-]{36})')\n"
                   "if (-not $m.Success) { throw \"no correlation id in '$message'\" }\n"
                   "$matched = $m.Groups[1].Value\n"),
        Step("read", "Read the event", "log",
             {"level": "info",
              "message": "source={{trg.param.eventSource}} type={{trg.param.eventEntryType}} "
                         "id={{trg.param.eventId}}"},
             cases=[{"id": "eventLogTrigger.logName.application",
                     "dimension": "eventLogTrigger.logName", "value": "Application"},
                    {"id": "eventLogTrigger.entryType.information",
                     "dimension": "eventLogTrigger.entryType", "value": "Information"},
                    {"id": "eventLogTrigger.source",
                     "dimension": "eventLogTrigger.source", "value": "source filter"},
                    {"id": "eventLogTrigger.messagePattern",
                     "dimension": "eventLogTrigger.messagePattern", "value": "regex"},
                    {"id": "eventLogTrigger.output.event",
                     "dimension": "eventLogTrigger.output",
                     "value": "eventSource / eventEntryType / eventId / eventMessage"}]),
        assert_step("""
$msg   = {{read.param.message}}
$acked = {{ack.param.ackedCid}}
if ($msg -match '\{\{') { throw "event log parameters left unresolved: '$msg'" }
if ($msg -notmatch 'type=Information') { throw "eventEntryType: '$msg'" }
if ($msg -notmatch 'source=NodePilot-TestSuite') { throw "eventSource: '$msg'" }
if ([string]::IsNullOrWhiteSpace($acked)) { throw "no correlation id was acknowledged" }
$assertOk = 'eventLogTrigger'
"""),
        ret({"trigger": "eventlog"}),
    ]
    return Workflow(
        55, "trigger-eventlog", "[TestSuite] trigger: event log",
        "Listens for the suite's own Application-log source. Registering that source is "
        "a one-time elevated step, so this workflow ships disabled.",
        "positive", "integration", None, steps, max_runtime=45, judge_by="cadence",
        requires=["event source " + EVENT_SOURCE + " registered (one-time, elevated)"],
        trigger=Step("trg", "Event Log", "eventLogTrigger",
                     {"logName": "Application", "entryType": "Information",
                      "source": EVENT_SOURCE, "messagePattern": "cid=[0-9a-fA-F-]{36}"}),
        excluded=[
            {"id": "eventLogTrigger.logName.system",
             "dimension": "eventLogTrigger.logName", "value": "System",
             "reason": "The suite cannot write to the System log without impersonating a "
                       "driver or service source.",
             "coveredBy": "tests/NodePilot.Engine.Tests"},
            {"id": "eventLogTrigger.entryType.other",
             "dimension": "eventLogTrigger.entryType",
             "value": "Error / Warning / SuccessAudit / FailureAudit",
             "reason": "Writing Error-level noise into the Application log every ten "
                       "minutes would pollute the very log operators triage with; the "
                       "audit types need a security-audit source the suite has no claim to.",
             "coveredBy": "tests/NodePilot.Engine.Tests"},
        ])


def driver_workflow():
    poke_file = """
$cid = {{cid.param.text}}
$watch = '""" + WATCH_DIR + """'
New-Item -ItemType Directory -Path $watch -Force | Out-Null
Set-Content -LiteralPath (Join-Path $watch "$cid.txt") -Value 'suite'
$pokedFile = $cid
"""
    prepare_db = """
$cid = {{cid.param.text}}
$dbDir = Split-Path -Parent '""" + SENTINEL_DB + """'
New-Item -ItemType Directory -Path $dbDir -Force | Out-Null
$pokedDb = $cid
"""
    check = """
$cid = {{cid.param.text}}
$ackRoot = '""" + ACK_DIR + """'
$missRecord = Join-Path $ackRoot 'consecutive-misses.txt'
$missing = @()
foreach ($kind in @('filewatcher', 'database', 'webhook')) {
  if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $ackRoot $kind) $cid))) {
    $missing += $kind
  }
}
# Prune acknowledgements so the fixture directory does not grow without bound.
foreach ($kind in @('filewatcher', 'database', 'webhook', 'eventlog')) {
  $dir = Join-Path $ackRoot $kind
  if (Test-Path -LiteralPath $dir) {
    Get-ChildItem -LiteralPath $dir -File |
      Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-2) } |
      Remove-Item -Force -ErrorAction SilentlyContinue
  }
}
Get-ChildItem -LiteralPath '""" + WATCH_DIR + """' -File -ErrorAction SilentlyContinue |
  Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-2) } |
  Remove-Item -Force -ErrorAction SilentlyContinue

$previous = 0
if (Test-Path -LiteralPath $missRecord) { $previous = [int](Get-Content -LiteralPath $missRecord -Raw) }
if ($missing.Count -eq 0) {
  Set-Content -LiteralPath $missRecord -Value '0'
  $acknowledged = 'all'
} else {
  $now = $previous + 1
  Set-Content -LiteralPath $missRecord -Value $now.ToString()
  # A database source re-baselines its first observation after every start, so one missed
  # round right after an API restart is expected. Two in a row is a dead trigger.
  if ($now -ge 2) {
    throw "no acknowledgement from: $($missing -join ', ') (two rounds in a row)"
  }
  $acknowledged = "missed once: $($missing -join ', ')"
}
"""
    steps = [
        Step("cid", "Correlation id", "generateText", {"mode": "guid"}),
        Step("pokeFile", "Poke: drop a watched file", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "script": poke_file},
             target_machine=LOCAL,
             cases=[{"id": "fileWatcherTrigger.fires", "assertedVia": "check", "dimension": "trigger.delivery",
                     "value": "fileWatcherTrigger fires and acknowledges"}]),
        Step("prepDb", "Poke: ensure the sentinel database", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "script": prepare_db},
             target_machine=LOCAL),
        Step("ddl", "Poke: sentinel table", "sql",
             {"provider": "sqlite", "dataSource": SENTINEL_DB, "timeoutSeconds": 15,
              "query": "CREATE TABLE IF NOT EXISTS suite_sentinel (id INTEGER PRIMARY KEY, sentinel TEXT)"}),
        Step("seedRow", "Poke: sentinel row", "sql",
             {"provider": "sqlite", "dataSource": SENTINEL_DB, "timeoutSeconds": 15,
              "query": "INSERT OR IGNORE INTO suite_sentinel (id, sentinel) VALUES (1, 'initial')"}),
        Step("pokeDb", "Poke: move the sentinel", "sql",
             {"provider": "sqlite", "dataSource": SENTINEL_DB, "timeoutSeconds": 15,
              "query": "UPDATE suite_sentinel SET sentinel = @cid WHERE id = 1",
              "parameters": {"cid": "{{cid.param.text}}"}},
             cases=[{"id": "databaseTrigger.fires", "assertedVia": "check", "dimension": "trigger.delivery",
                     "value": "databaseTrigger fires and acknowledges"}]),
        Step("pokeHook", "Poke: post the webhook", "restApi",
             {"url": WEBHOOK_URL, "method": "POST", "timeoutSeconds": 15,
              "headers": {"Content-Type": "application/json",
                          "X-Webhook-Secret": "{{globals.NP_TESTSUITE_WEBHOOK_SECRET}}"},
              "body": "{\"cid\":\"{{cid.param.text}}\"}"},
             cases=[{"id": "webhookTrigger.fires", "assertedVia": "check", "dimension": "trigger.delivery",
                     "value": "webhookTrigger fires and acknowledges"}]),
        # The manual trigger has no source of its own, so the driver is what exercises it.
        Step("pokeManual", "Poke: start the manual-trigger workflow", "startWorkflow",
             {"workflowNameOrId": "[TestSuite] trigger: manual",
              "waitForCompletion": True, "timeoutSeconds": 60,
              "parameters": {"probe": "{{cid.param.text}}"}},
             cases=[{"id": "manualTrigger.fires", "assertedVia": "check", "dimension": "trigger.delivery",
                     "value": "manualTrigger driven through startWorkflow"}]),
        Step("settle", "Wait for the sources to pick up", "delay", {"seconds": 60}),
        Step("check", "Collect acknowledgements", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": check},
             target_machine=LOCAL),
        assert_step("""
$state = {{check.param.acknowledged}}
if ([string]::IsNullOrWhiteSpace($state)) { throw "the acknowledgement check produced no verdict" }
$assertOk = 'trigger-drivers'
"""),
        ok_return("trigger-drivers"),
    ]
    return Workflow(
        70, "trigger-drivers", "[TestSuite] trigger drivers",
        "Pokes the three passive sources with one correlation id and waits for their "
        "acknowledgement files. A single missed round is tolerated because a database "
        "source re-baselines after a restart; two in a row fail the run.",
        "positive", "continuous", "D", steps, max_runtime=180,
        requires=["globals:NP_TESTSUITE_WEBHOOK_URL", "globals:NP_TESTSUITE_WEBHOOK_SECRET"])


def event_log_driver_workflow():
    poke = """
$cid = {{cid.param.text}}
Write-EventLog -LogName Application -Source '""" + EVENT_SOURCE + """' -EventId 4242 `
  -EntryType Information -Message "NodePilot test suite probe cid=$cid"
$pokedEvent = $cid
"""
    check = """
$cid = {{cid.param.text}}
$ack = Join-Path (Join-Path '""" + ACK_DIR + """' 'eventlog') $cid
if (-not (Test-Path -LiteralPath $ack)) { throw "the event log trigger did not acknowledge cid $cid" }
$acknowledged = 'eventlog'
"""
    steps = [
        Step("cid", "Correlation id", "generateText", {"mode": "guid"}),
        Step("poke", "Poke: write an Application event", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "script": poke},
             target_machine=LOCAL,
             cases=[{"id": "eventLogTrigger.fires", "assertedVia": "check", "dimension": "trigger.delivery",
                     "value": "eventLogTrigger fires and acknowledges"}]),
        Step("settle", "Wait for the listener", "delay", {"seconds": 45}),
        Step("check", "Collect the acknowledgement", "runScript",
             {"engine": "auto", "timeoutSeconds": 30, "script": check},
             target_machine=LOCAL),
        assert_step("""
$state = {{check.param.acknowledged}}
if ($state -ne 'eventlog') { throw "event log acknowledgement missing" }
$assertOk = 'trigger-driver-eventlog'
"""),
        ok_return("trigger-driver-eventlog"),
    ]
    return Workflow(
        71, "trigger-driver-eventlog", "[TestSuite] trigger driver: event log",
        "Writes one Application-log event per cadence and waits for the event log "
        "trigger to acknowledge it. Needs the suite's event source registered.",
        "positive", "integration", "C", steps, max_runtime=180,
        requires=["event source " + EVENT_SOURCE + " registered (one-time, elevated)"])


def workflows():
    return [schedule_trigger_workflow(), manual_trigger_workflow(),
            webhook_trigger_workflow(), file_watcher_trigger_workflow(),
            database_trigger_workflow(), event_log_trigger_workflow(),
            driver_workflow(), event_log_driver_workflow()]
