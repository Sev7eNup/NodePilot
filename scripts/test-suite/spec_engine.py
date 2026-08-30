"""Engine-local activity coverage: log, delay, generateText, jsonQuery, xmlQuery,
plus the shared child workflow that forEach and startWorkflow resolve by name."""

from suitelib import Step, Workflow, RUNS_ROOT
from spec_core import (LOCAL, manual, janitor, cid, mkrun, cleanup,
                       assert_step, ok_return, CID, RUN_DIR)

JSON_DOC = ('{"items":[{"name":"alpha","id":1,"active":true},'
            '{"name":"beta","id":2,"active":false},'
            '{"name":"gamma","id":3,"active":true}]}')
XML_DOC = ('<?xml version="1.0"?><root xmlns:np="urn:nodepilot:test">'
           '<item id="1">alpha</item><item id="2">beta</item>'
           '<np:tagged id="3">gamma</np:tagged></root>')


def _seed_file(name, content):
    """A runScript that drops a fixture file into this run's sandbox. Templates in a
    runScript body are substituted as single-quoted PowerShell literals, so the
    correlation id is read into a variable first and never inlined into a string."""
    script = (
        "$cid = " + CID + "\n"
        "$dir = Join-Path '" + RUNS_ROOT + "' $cid\n"
        "$path = Join-Path $dir '" + name + "'\n"
        "Set-Content -LiteralPath $path -Encoding utf8 -Value @'\n"
        + content + "\n'@\n"
        "$seeded = 'ok'\n")
    return Step("seed", "Seed: " + name, "runScript",
                {"engine": "auto", "timeoutSeconds": 20, "script": script},
                target_machine=LOCAL)


def child_echo():
    """Every forEach / startWorkflow case resolves this by name. It must exist and be
    enabled before its parents are published, otherwise those steps fail outright."""
    steps = [
        # The parent's forEach continueOnError case needs one item that genuinely fails.
        # A child execution is its own run, so its failure never touches the parent's
        # failed-step count - only the forEach counters see it.
        Step("guard", "Fail on the item named boom", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": "$item = {{trg.param.item}}\n"
                        "if ($item -eq 'boom') { throw \"child failed on purpose for item 'boom'\" }\n"
                        "$echoedItem = $item\n"},
             target_machine=LOCAL),
        Step("echo", "Log: echo item", "log",
             {"level": "info",
              "message": "child item={{trg.param.item}} index={{trg.param.index}}"}),
        Step("ret", "Return", "returnData",
             {"data": {"item": "{{trg.param.item}}", "index": "{{trg.param.index}}",
                       "extra": "{{trg.param.extra}}", "echoed": "yes"}}),
    ]
    return Workflow(
        0, "child-echo", "[TestSuite] Child: Echo Item",
        "Shared child for forEach and startWorkflow. Echoes its inputs back through "
        "returnData so the parent can assert the parameter round-trip.",
        "positive", "continuous", None, steps, max_runtime=20,
        trigger=manual([
            {"name": "item", "type": "string", "required": False, "default": ""},
            {"name": "index", "type": "string", "required": False, "default": ""},
            {"name": "extra", "type": "string", "required": False, "default": ""},
        ]))


def log_workflow():
    steps = [
        Step("v0", "log: default level", "log",
             {"message": "TestSuite log without an explicit level"},
             cases=[{"id": "log.level.default", "dimension": "log.level",
                     "value": "(absent)"}]),
        Step("v1", "log: info", "log", {"level": "info", "message": "TestSuite info"},
             cases=[{"id": "log.level.info", "dimension": "log.level", "value": "info"}]),
        Step("v2", "log: warning", "log",
             {"level": "warning", "message": "TestSuite warning"},
             cases=[{"id": "log.level.warning", "dimension": "log.level",
                     "value": "warning"}]),
        Step("v3", "log: error", "log", {"level": "error", "message": "TestSuite error"},
             cases=[{"id": "log.level.error", "dimension": "log.level", "value": "error"}]),
        Step("v4", "log: template resolution", "log",
             {"level": "info",
              "message": "resolved level={{v1.param.level}} ok={{v1.success}}"},
             cases=[{"id": "log.message.template", "dimension": "log.message",
                     "value": "template"}]),
        Step("v5", "log: CRLF sanitisation", "log",
             {"level": "info", "message": "line1\nline2\rline3"},
             cases=[{"id": "log.message.crlf", "dimension": "log.message",
                     "value": "embedded CR/LF"}]),
        assert_step("""
$lvlDefault = {{v0.param.level}}
$lvlInfo    = {{v1.param.level}}
$lvlWarn    = {{v2.param.level}}
$lvlError   = {{v3.param.level}}
$templated  = {{v4.param.message}}
$sanitised  = {{v5.param.message}}

if ($lvlDefault -ne 'info')    { throw "log default level: expected info, got '$lvlDefault'" }
if ($lvlInfo    -ne 'info')    { throw "log info: got '$lvlInfo'" }
if ($lvlWarn    -ne 'warning') { throw "log warning: got '$lvlWarn'" }
if ($lvlError   -ne 'error')   { throw "log error: got '$lvlError'" }
if ($templated -notmatch 'level=info') { throw "log template unresolved: '$templated'" }
if ($templated -notmatch 'ok=true')    { throw "log success template unresolved: '$templated'" }
if ($sanitised -match '[\\r\\n]')        { throw "log did not strip CR/LF: '$sanitised'" }
if ($sanitised -notmatch 'line1.line2.line3') { throw "log lost content: '$sanitised'" }
$assertOk = 'log'
"""),
        ok_return("log"),
    ]
    return Workflow(
        10, "log", "[TestSuite] log",
        "All three levels plus the absent-level default, template resolution in the "
        "message and CR/LF sanitisation.",
        "positive", "continuous", "A", steps, max_runtime=30,
        excluded=[
            {"id": "log.message.truncation", "dimension": "log.message",
             "value": ">8 KiB",
             "reason": "Pure boundary in LogActivity; an 8 KiB literal in the workflow "
                       "JSON buys nothing a unit test does not already prove.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/LogActivityTests.cs"},
            {"id": "log.message.redaction", "dimension": "log.message", "value": "secret",
             "reason": "Redaction is asserted against OutputRedactor directly; feeding a "
                       "real secret through a scheduled workflow would persist it to the "
                       "support log on every run.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/LogActivityTests.cs"},
        ])


def delay_workflow():
    steps = [
        Step("v0", "delay: 0s", "delay", {"seconds": 0},
             cases=[{"id": "delay.seconds.zero", "dimension": "delay.seconds",
                     "value": "0"}]),
        Step("v1", "delay: 2s", "delay", {"seconds": 2},
             cases=[{"id": "delay.seconds.typical", "dimension": "delay.seconds",
                     "value": "2"}]),
        Step("v2", "delay: non-number falls back", "delay", {"seconds": "3"},
             cases=[{"id": "delay.seconds.non-number", "dimension": "delay.seconds",
                     "value": "string -> default 5"}]),
        assert_step("""
$zero     = {{v0.output}}
$two      = {{v1.output}}
$fallback = {{v2.output}}
if ($zero -notmatch 'Delayed for 0 seconds') { throw "delay 0: got '$zero'" }
if ($two  -notmatch 'Delayed for 2 seconds') { throw "delay 2: got '$two'" }
# A JSON string is not a JSON number, so the executor falls back to its 5 s default.
if ($fallback -notmatch 'Delayed for 5 seconds') { throw "delay string fallback: got '$fallback'" }
$assertOk = 'delay'
"""),
        ok_return("delay"),
    ]
    return Workflow(
        11, "delay", "[TestSuite] delay",
        "Zero, a typical duration and the non-number fallback to the 5 s default.",
        "positive", "continuous", "A", steps, max_runtime=40,
        excluded=[
            {"id": "delay.seconds.above-max", "dimension": "delay.seconds",
             "value": ">86400",
             "reason": "Clamped to 86400 s; the step would pin a runner slot for 24 hours "
                       "and no cadence can contain that.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/NonRemoteActivityTests.cs"},
        ])


def generate_text_workflow():
    steps = [
        Step("v0", "gen: alphanumeric", "generateText",
             {"mode": "alphanumeric", "length": 16},
             cases=[{"id": "generateText.mode.alphanumeric",
                     "dimension": "generateText.mode", "value": "alphanumeric"}]),
        Step("v1", "gen: alphabetic", "generateText", {"mode": "alphabetic", "length": 12},
             cases=[{"id": "generateText.mode.alphabetic",
                     "dimension": "generateText.mode", "value": "alphabetic"}]),
        Step("v2", "gen: numeric", "generateText", {"mode": "numeric", "length": 8},
             cases=[{"id": "generateText.mode.numeric",
                     "dimension": "generateText.mode", "value": "numeric"}]),
        Step("v3", "gen: hex", "generateText", {"mode": "hex", "length": 32},
             cases=[{"id": "generateText.mode.hex",
                     "dimension": "generateText.mode", "value": "hex"}]),
        Step("v4", "gen: guid", "generateText", {"mode": "guid"},
             cases=[{"id": "generateText.mode.guid",
                     "dimension": "generateText.mode", "value": "guid"}]),
        Step("v5", "gen: password", "generateText",
             {"mode": "password", "length": 24, "excludeAmbiguous": True},
             cases=[{"id": "generateText.mode.password",
                     "dimension": "generateText.mode", "value": "password"},
                    {"id": "generateText.excludeAmbiguous.true",
                     "dimension": "generateText.excludeAmbiguous", "value": "true"}]),
        Step("v6", "gen: custom charset", "generateText",
             {"mode": "custom", "customCharset": "ABCDEF", "length": 10},
             cases=[{"id": "generateText.mode.custom",
                     "dimension": "generateText.mode", "value": "custom"}]),
        Step("v7", "gen: length clamped low", "generateText",
             {"mode": "alphanumeric", "length": 0},
             cases=[{"id": "generateText.length.clamp-low",
                     "dimension": "generateText.length", "value": "0 -> 1"}]),
        Step("v8", "gen: length clamped high", "generateText",
             {"mode": "alphanumeric", "length": 4096},
             cases=[{"id": "generateText.length.clamp-high",
                     "dimension": "generateText.length", "value": "4096 -> 1024"}]),
        Step("v9", "gen: unknown mode falls back", "generateText",
             {"mode": "not-a-mode", "length": 9},
             cases=[{"id": "generateText.mode.unknown",
                     "dimension": "generateText.mode",
                     "value": "unknown -> alphanumeric"}]),
        assert_step("""
$an   = {{v0.param.text}}
$al   = {{v1.param.text}}
$nu   = {{v2.param.text}}
$hx   = {{v3.param.text}}
$gu   = {{v4.param.text}}
$pw   = {{v5.param.text}}
$cu   = {{v6.param.text}}
$low  = {{v7.param.text}}
$high = {{v8.param.text}}
$unk  = {{v9.param.text}}

if ($an.Length -ne 16) { throw "alphanumeric length: $($an.Length)" }
if ($an -notmatch '^[A-Za-z0-9]+$') { throw "alphanumeric charset: '$an'" }
if ($al.Length -ne 12 -or $al -notmatch '^[A-Za-z]+$') { throw "alphabetic: '$al'" }
if ($nu.Length -ne 8  -or $nu -notmatch '^[0-9]+$')    { throw "numeric: '$nu'" }
if ($hx.Length -ne 32 -or $hx -cnotmatch '^[0-9a-f]+$') { throw "hex must be lowercase: '$hx'" }
if ($gu -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') {
  throw "guid shape: '$gu'"
}
if ($pw.Length -ne 24) { throw "password length: $($pw.Length)" }
if ($pw -cmatch '[0Oo1lIi5Ss2Zz8B6Gg9q|]') { throw "excludeAmbiguous leaked: '$pw'" }
if ($cu.Length -ne 10 -or $cu -cnotmatch '^[ABCDEF]+$') { throw "custom charset: '$cu'" }
if ($low.Length -ne 1) { throw "length 0 should clamp to 1, got $($low.Length)" }
if ($high.Length -ne 1024) { throw "length 4096 should clamp to 1024, got $($high.Length)" }
if ($unk.Length -ne 9 -or $unk -notmatch '^[A-Za-z0-9]+$') {
  throw "unknown mode should fall back to alphanumeric: '$unk'"
}
$assertOk = 'generateText'
"""),
        ok_return("generateText"),
    ]
    return Workflow(
        12, "generateText", "[TestSuite] generateText",
        "All seven modes, both length clamps, excludeAmbiguous and the silent fallback "
        "an unrecognised mode takes.",
        "positive", "continuous", "A", steps, max_runtime=30)


def json_query_workflow():
    steps = [
        janitor(), cid(), mkrun(), _seed_file("doc.json", JSON_DOC),
        Step("v0", "json: inline single", "jsonQuery",
             {"source": "inline", "content": JSON_DOC,
              "jsonPath": "$.items[0].name", "resultMode": "single"},
             cases=[{"id": "jsonQuery.source.inline", "dimension": "jsonQuery.source",
                     "value": "inline"},
                    {"id": "jsonQuery.resultMode.single",
                     "dimension": "jsonQuery.resultMode", "value": "single"}]),
        Step("v1", "json: inline all (wildcard)", "jsonQuery",
             {"source": "inline", "content": JSON_DOC,
              "jsonPath": "$.items[*].name", "resultMode": "all"},
             cases=[{"id": "jsonQuery.resultMode.all",
                     "dimension": "jsonQuery.resultMode", "value": "all"}]),
        Step("v2", "json: filter expression", "jsonQuery",
             {"source": "inline", "content": JSON_DOC,
              "jsonPath": "$.items[?(@.active==true)].name", "resultMode": "all"},
             cases=[{"id": "jsonQuery.jsonPath.filter",
                     "dimension": "jsonQuery.jsonPath", "value": "filter expression"}]),
        Step("v3", "json: no match", "jsonQuery",
             {"source": "inline", "content": JSON_DOC,
              "jsonPath": "$.items[?(@.name=='delta')].name", "resultMode": "all"},
             cases=[{"id": "jsonQuery.jsonPath.no-match",
                     "dimension": "jsonQuery.jsonPath", "value": "no match -> count 0"}]),
        Step("v4", "json: source file", "jsonQuery",
             {"source": "file", "path": RUN_DIR + "\\doc.json",
              "jsonPath": "$.items[*].id", "resultMode": "all"},
             cases=[{"id": "jsonQuery.source.file", "dimension": "jsonQuery.source",
                     "value": "file"}]),
        cleanup(),
        assert_step("""
$single = {{v0.param.result}}
$all    = {{v1.param.result}}
$allN   = {{v1.param.count}}
$filter = {{v2.param.result}}
$noneN  = {{v3.param.count}}
$fileR  = {{v4.param.result}}

if ($single -ne 'alpha') { throw "jsonQuery single: expected alpha, got '$single'" }
if ([int]$allN -ne 3)    { throw "jsonQuery wildcard count: expected 3, got '$allN'" }
if ($all -notmatch 'alpha' -or $all -notmatch 'gamma') { throw "jsonQuery all: '$all'" }
if ($filter -notmatch 'alpha' -or $filter -notmatch 'gamma') {
  throw "jsonQuery filter should keep both active items: '$filter'"
}
if ($filter -match 'beta') { throw "jsonQuery filter leaked an inactive item: '$filter'" }
if ([int]$noneN -ne 0) { throw "jsonQuery no-match count: expected 0, got '$noneN'" }
if ($fileR -notmatch '1' -or $fileR -notmatch '3') { throw "jsonQuery from file: '$fileR'" }
$assertOk = 'jsonQuery'
"""),
        ok_return("jsonQuery", with_cid=True),
    ]
    return Workflow(
        13, "jsonQuery", "[TestSuite] jsonQuery",
        "Both sources, both result modes, a JSONPath filter expression and the "
        "zero-match case.",
        "positive", "continuous", "A", steps, max_runtime=60)


def xml_query_workflow():
    steps = [
        janitor(), cid(), mkrun(), _seed_file("doc.xml", XML_DOC),
        Step("v0", "xml: inline single", "xmlQuery",
             {"source": "inline", "content": XML_DOC,
              "xpath": "//item[@id='1']", "resultMode": "single"},
             cases=[{"id": "xmlQuery.source.inline", "dimension": "xmlQuery.source",
                     "value": "inline"},
                    {"id": "xmlQuery.resultMode.single",
                     "dimension": "xmlQuery.resultMode", "value": "single"}]),
        Step("v1", "xml: inline all", "xmlQuery",
             {"source": "inline", "content": XML_DOC,
              "xpath": "//item", "resultMode": "all"},
             cases=[{"id": "xmlQuery.resultMode.all",
                     "dimension": "xmlQuery.resultMode", "value": "all"}]),
        Step("v2", "xml: namespaced xpath", "xmlQuery",
             {"source": "inline", "content": XML_DOC,
              "xpath": "//np:tagged", "resultMode": "single",
              "namespaces": {"np": "urn:nodepilot:test"}},
             cases=[{"id": "xmlQuery.namespaces.prefix",
                     "dimension": "xmlQuery.namespaces", "value": "prefix map"}]),
        Step("v3", "xml: source file", "xmlQuery",
             {"source": "file", "path": RUN_DIR + "\\doc.xml",
              "xpath": "//item", "resultMode": "all"},
             cases=[{"id": "xmlQuery.source.file", "dimension": "xmlQuery.source",
                     "value": "file"}]),
        Step("v4", "xml: single with two matches", "xmlQuery",
             {"source": "inline", "content": XML_DOC, "xpath": "//item",
              "resultMode": "single"},
             cases=[{"id": "xmlQuery.resultMode.single-first-match",
                     "dimension": "xmlQuery.resultMode",
                     "value": "single with several matches -> first, count 1"}]),
        Step("v5", "xml: no match", "xmlQuery",
             {"source": "inline", "content": XML_DOC, "xpath": "//nothing",
              "resultMode": "single"},
             cases=[{"id": "xmlQuery.resultMode.single-no-match",
                     "dimension": "xmlQuery.resultMode",
                     "value": "single with no match -> empty, count 0"}]),
        cleanup(),
        assert_step("""
$single = {{v0.param.result}}
$all    = {{v1.param.result}}
$allN   = {{v1.param.count}}
$ns     = {{v2.param.result}}
$fileN  = {{v3.param.count}}
$firstOfMany = {{v4.param.result}}
$firstCount  = {{v4.param.count}}
$noneResult  = {{v5.param.result}}
$noneCount   = {{v5.param.count}}

if ($single -notmatch 'alpha') { throw "xmlQuery single: '$single'" }
if ([int]$allN -ne 2) { throw "xmlQuery //item count: expected 2, got '$allN'" }
if ($all -notmatch 'beta') { throw "xmlQuery all missing beta: '$all'" }
if ($ns -notmatch 'gamma') { throw "xmlQuery namespaced lookup: '$ns'" }
if ([int]$fileN -ne 2) { throw "xmlQuery from file count: expected 2, got '$fileN'" }
# single mode narrows to the first hit instead of failing, and reports count 1.
if ($firstOfMany -notmatch 'alpha') { throw "single mode should return the first match, got '$firstOfMany'" }
if ([int]$firstCount -ne 1) { throw "single mode count with several matches: $firstCount" }
if (-not [string]::IsNullOrEmpty($noneResult)) { throw "single mode with no match returned '$noneResult'" }
if ([int]$noneCount -ne 0) { throw "single mode count with no match: $noneCount" }
$assertOk = 'xmlQuery'
"""),
        ok_return("xmlQuery", with_cid=True),
    ]
    return Workflow(
        14, "xmlQuery", "[TestSuite] xmlQuery",
        "Both sources, both result modes and a namespace-prefixed XPath.",
        "positive", "continuous", "A", steps, max_runtime=60)


def workflows():
    return [child_echo(), log_workflow(), delay_workflow(), generate_text_workflow(),
            json_query_workflow(), xml_query_workflow()]
