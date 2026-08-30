"""Network- and service-facing engine-local activities: restApi, sql, emailNotification
and llmQuery.

restApi and the sqlite half of sql run everywhere a dev instance runs. Everything that
needs a second system - a real database, an SMTP sink, an LLM endpoint, an HTTP proxy -
is an `integration` case: installed, but left disabled until the host provides it.
"""

from suitelib import Step, Workflow, RUNS_ROOT
from spec_core import LOCAL, janitor, cid, mkrun, cleanup, assert_step, ok_return, RUN_DIR

SELF_URL = "{{globals.NP_TESTSUITE_SELF_URL}}"


def rest_api_workflow():
    def call(sid, label, config, cases=None):
        cfg = {"url": SELF_URL, "timeoutSeconds": 10}
        cfg.update(config)
        return Step(sid, label, "restApi", cfg, cases=cases)

    steps = [
        call("v0", "rest: GET", {"method": "GET"},
             [{"id": "restApi.method.GET", "dimension": "restApi.method", "value": "GET"}]),
        call("v1", "rest: GET + header object",
             {"method": "GET", "headers": {"X-Suite": "1", "Accept": "application/json"}},
             [{"id": "restApi.headers.object", "dimension": "restApi.headers",
               "value": "JSON object"}]),
        call("v2", "rest: GET + header lines",
             {"method": "GET", "headers": "X-Suite: 2\nAccept: text/plain"},
             [{"id": "restApi.headers.lines", "dimension": "restApi.headers",
               "value": "multi-line string"}]),
        call("v3", "rest: GET + retry",
             {"method": "GET",
              "retry": {"maxAttempts": 3, "backoff": "exponential",
                        "initialDelayMs": 200, "maxDelayMs": 2000}},
             [{"id": "restApi.retry", "dimension": "restApi.retry",
               "value": "retry block on an HTTP call"}]),
        call("v4", "rest: HEAD", {"method": "HEAD"},
             [{"id": "restApi.method.HEAD", "dimension": "restApi.method",
               "value": "HEAD"}]),
        call("v5", "rest: proxyMode default", {"method": "GET", "proxyMode": "default"},
             [{"id": "restApi.proxyMode.default", "dimension": "restApi.proxyMode",
               "value": "default"}]),
        call("v6", "rest: proxyMode direct", {"method": "GET", "proxyMode": "direct"},
             [{"id": "restApi.proxyMode.direct", "dimension": "restApi.proxyMode",
               "value": "direct"}]),
        assert_step("""
$get      = {{v0.param.statusCode}}
$hdrObj   = {{v1.param.statusCode}}
$hdrLines = {{v2.param.statusCode}}
$retried  = {{v3.param.statusCode}}
$head     = {{v4.param.statusCode}}
$proxyDef = {{v5.param.statusCode}}
$proxyDir = {{v6.param.statusCode}}
$body     = {{v0.output}}

foreach ($pair in @(@('GET',$get), @('header object',$hdrObj), @('header lines',$hdrLines),
                    @('retry',$retried), @('HEAD',$head), @('proxyMode default',$proxyDef),
                    @('proxyMode direct',$proxyDir))) {
  if ($pair[1] -ne '200') { throw "restApi $($pair[0]): expected 200, got '$($pair[1])'" }
}
if ([string]::IsNullOrWhiteSpace($body)) { throw "restApi GET returned an empty body" }
$assertOk = 'restApi'
"""),
        ok_return("restApi"),
    ]
    return Workflow(
        30, "restApi", "[TestSuite] restApi",
        "The read verbs, both header shapes, a retry block and two proxy modes against "
        "the instance's own health endpoint. The write verbs only ever come back non-2xx "
        "here, and restApi treats any non-2xx as a failed step, so they are covered by "
        "the negative contract instead.",
        "positive", "continuous", "A", steps, max_runtime=90,
        requires=["globals:NP_TESTSUITE_SELF_URL",
                  "config:RestApi:AllowedHosts includes the probe host"],
        excluded=[
            {"id": "restApi.proxyMode.custom", "dimension": "restApi.proxyMode",
             "value": "custom",
             "reason": "Needs a reachable HTTP proxy. proxyAddress and noProxy are also "
                       "missing from the config reference, which PR 5 fixes.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/RestApiProxyTests.cs"},
            {"id": "restApi.redirects", "dimension": "restApi.url",
             "value": "redirect chain",
             "reason": "Needs an endpoint that redirects; the API offers none, and adding "
                       "one for the suite would change the product.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/RestApiRedirectTests.cs"},
            {"id": "restApi.response.over-limit", "dimension": "restApi.url",
             "value": "response over 16 MiB",
             "reason": "Moving 16 MiB through the engine on every cadence is not a "
                       "proportionate cost.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/NonRemoteActivityTests.cs"},
        ])


def sql_workflow():
    db = RUN_DIR + r"\suite.db"

    def q(sid, label, config, cases=None):
        cfg = {"provider": "sqlite", "timeoutSeconds": 15}
        cfg.update(config)
        return Step(sid, label, "sql", cfg, cases=cases)

    steps = [
        janitor(), cid(), mkrun(),
        q("v0", "sql: literal select",
          {"dataSource": ":memory:", "query": "SELECT 1 AS val, 'suite' AS msg"},
          [{"id": "sql.provider.sqlite", "dimension": "sql.provider", "value": "sqlite"},
           {"id": "sql.dataSource.memory", "dimension": "sql.dataSource",
            "value": ":memory: (builder mode)"}]),
        q("v1", "sql: multi row",
          {"dataSource": ":memory:",
           "query": "SELECT 1 AS x UNION SELECT 2 UNION SELECT 3"},
          [{"id": "sql.output.rowCount", "dimension": "sql.output", "value": "rowCount"}]),
        q("v2", "sql: bound parameters",
          {"dataSource": ":memory:",
           "query": "SELECT @needle AS echoed, :second AS other",
           "parameters": {"needle": "bound-value", "second": "42"}},
          [{"id": "sql.parameters.bound", "dimension": "sql.parameters",
            "value": "@name / :name binding"}]),
        q("v3", "sql: create table", {"dataSource": db,
                                      "query": "CREATE TABLE IF NOT EXISTS t (id INTEGER, name TEXT)"},
          [{"id": "sql.dataSource.file", "assertedVia": "v5",
            "dimension": "sql.dataSource", "value": "file-backed sqlite"}]),
        q("v4", "sql: insert (rowsAffected)",
          {"dataSource": db,
           "query": "INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b')"},
          [{"id": "sql.output.rowsAffected", "dimension": "sql.output",
            "value": "rowsAffected"}]),
        q("v5", "sql: read back", {"dataSource": db, "query": "SELECT id, name FROM t ORDER BY id"},
          [{"id": "sql.output.flat-projection", "dimension": "sql.output",
            "value": "row{i}_{col} projection"}]),
        q("v6", "sql: row cap", {
            "dataSource": ":memory:",
            "query": "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x<1500) "
                     "SELECT x FROM c"},
          [{"id": "sql.output.truncated", "dimension": "sql.output",
            "value": "row cap at 1000 -> truncated"}]),
        cleanup(),
        assert_step("""
$val      = {{v0.param.val}}
$msg      = {{v0.param.msg}}
$rows     = {{v1.param.rowCount}}
$echoed   = {{v2.param.echoed}}
$other    = {{v2.param.other}}
$affected = {{v4.param.rowsAffected}}
$readRows = {{v5.param.rowCount}}
$firstName = {{v5.param.row0_name}}
$capRows   = {{v6.param.rowCount}}
$truncated = {{v6.param.truncated}}

if ($val -ne '1')      { throw "sql scalar column: '$val'" }
if ($msg -ne 'suite')  { throw "sql text column: '$msg'" }
if ([int]$rows -ne 3)  { throw "sql multi-row rowCount: $rows" }
if ($echoed -ne 'bound-value') { throw "sql @name binding: '$echoed'" }
if ($other -ne '42')   { throw "sql :name binding: '$other'" }
if ([int]$affected -ne 2) { throw "sql rowsAffected: $affected" }
if ([int]$readRows -ne 2) { throw "sql read back rowCount: $readRows" }
if ($firstName -ne 'a') { throw "sql flat projection row0_name: '$firstName'" }
if ([int]$capRows -ne 1000) { throw "sql row cap should stop at 1000, got $capRows" }
if ($truncated -ne 'True' -and $truncated -ne 'true') {
  throw "sql should report truncated when the cap is hit, got '$truncated'"
}
$assertOk = 'sql'
"""),
        ok_return("sql", with_cid=True),
    ]
    return Workflow(
        31, "sql", "[TestSuite] sql",
        "Builder mode against sqlite in memory and on disk, bound parameters in both "
        "syntaxes, rowCount, rowsAffected, the flat row projection and the 1000-row cap.",
        "positive", "continuous", "B", steps, max_runtime=120,
        requires=["config:SqlActivity:RequireConnectionRef=false for the inline data source"])


def sql_integration_workflow():
    steps = [
        Step("v0", "sql: named connection", "sql",
             {"provider": "postgres", "connectionRef": "{{globals.NP_TESTSUITE_SQL_CONNREF}}",
              "query": "SELECT 1 AS val", "timeoutSeconds": 15},
             cases=[{"id": "sql.connectionRef", "dimension": "sql.connection",
                     "value": "connectionRef (the production-safe mode)"},
                    {"id": "sql.provider.postgres", "dimension": "sql.provider",
                     "value": "postgres"}]),
        assert_step("""
$val = {{v0.param.val}}
if ($val -ne '1') { throw "sql via connectionRef returned '$val'" }
$assertOk = 'sql-integration'
"""),
        ok_return("sql-integration"),
    ]
    return Workflow(
        32, "sql-integration", "[TestSuite] sql (named connection)",
        "The connectionRef path and a non-sqlite provider. Both need a connection the "
        "host configures, so this workflow ships disabled.",
        "positive", "integration", "C", steps, max_runtime=60,
        requires=["globals:NP_TESTSUITE_SQL_CONNREF",
                  "config:SqlActivity:ConnectionStrings:<name>"],
        excluded=[
            {"id": "sql.provider.sqlserver", "dimension": "sql.provider",
             "value": "sqlserver",
             "reason": "No SQL Server instance is part of the development environment, "
                       "and the suite must not depend on one being installed.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/SqlActivityTests.cs"},
        ])


def email_workflow():
    steps = [
        Step("v0", "email: plain text", "emailNotification",
             {"to": "{{globals.NP_TESTSUITE_MAIL_TO}}", "subject": "[TestSuite] plain",
              "body": "Plain body from the suite.", "isHtml": False,
              "timeoutSeconds": 20},
             cases=[{"id": "emailNotification.isHtml.false",
                     "dimension": "emailNotification.isHtml", "value": "false"},
                    {"id": "emailNotification.timeoutSeconds",
                     "dimension": "emailNotification.timeoutSeconds", "value": "20"}]),
        Step("v1", "email: HTML", "emailNotification",
             {"to": "{{globals.NP_TESTSUITE_MAIL_TO}}", "subject": "[TestSuite] html",
              "body": "<b>HTML</b> body from the suite.", "isHtml": True,
              "timeoutSeconds": 20},
             cases=[{"id": "emailNotification.isHtml.true",
                     "dimension": "emailNotification.isHtml", "value": "true"}]),
        assert_step("""
$plain = {{v0.success}}
$html  = {{v1.success}}
if ($plain -ne 'true') { throw "plain-text mail did not send" }
if ($html  -ne 'true') { throw "HTML mail did not send" }
$assertOk = 'emailNotification'
"""),
        ok_return("emailNotification"),
    ]
    return Workflow(
        34, "emailNotification", "[TestSuite] emailNotification",
        "Both body types against a real SMTP sink. Point Smtp:* at a local catcher such "
        "as smtp4dev; without one the workflow stays disabled rather than red.",
        "positive", "integration", "C", steps, max_runtime=60,
        requires=["globals:NP_TESTSUITE_MAIL_TO", "config:Smtp:Host"])


def llm_workflow():
    steps = [
        Step("v0", "llm: plain prompt", "llmQuery",
             {"prompt": "Reply with exactly the word OK and nothing else.",
              "systemPrompt": "", "jsonMode": False, "maxTokens": 16,
              "temperature": 0, "timeoutSeconds": 60},
             cases=[{"id": "llmQuery.jsonMode.false", "dimension": "llmQuery.jsonMode",
                     "value": "false"},
                    {"id": "llmQuery.maxTokens", "dimension": "llmQuery.maxTokens",
                     "value": "16"},
                    {"id": "llmQuery.temperature", "dimension": "llmQuery.temperature",
                     "value": "0"},
                    {"id": "llmQuery.timeoutSeconds",
                     "dimension": "llmQuery.timeoutSeconds", "value": "60"}]),
        Step("v1", "llm: json mode", "llmQuery",
             {"prompt": "Return the JSON object {\"status\":\"ok\"}.",
              "jsonMode": True, "maxTokens": 64, "temperature": 0,
              "timeoutSeconds": 60},
             cases=[{"id": "llmQuery.jsonMode.true", "dimension": "llmQuery.jsonMode",
                     "value": "true"}]),
        assert_step("""
$plain  = {{v0.output}}
$model  = {{v0.param.model}}
$finish = {{v0.param.finishReason}}
$json   = {{v1.output}}

if ([string]::IsNullOrWhiteSpace($plain)) { throw "llmQuery returned no text" }
if ([string]::IsNullOrWhiteSpace($model)) { throw "llmQuery did not report a model" }
if ([string]::IsNullOrWhiteSpace($finish)) { throw "llmQuery did not report finishReason" }
# jsonMode does not validate the answer, so this asserts the shape the caller relies on.
try { $null = $json | ConvertFrom-Json } catch { throw "jsonMode did not return JSON: '$json'" }
$assertOk = 'llmQuery'
"""),
        ok_return("llmQuery"),
    ]
    return Workflow(
        35, "llmQuery", "[TestSuite] llmQuery",
        "A plain completion and JSON mode with explicit token and temperature limits. "
        "Needs Llm:Enabled and a reachable endpoint, so it ships disabled.",
        "positive", "integration", "C", steps, max_runtime=180,
        requires=["config:Llm:Enabled=true", "config:Llm:ActiveProfileId"],
        excluded=[
            {"id": "llmQuery.baseUrl.override", "dimension": "llmQuery.baseUrl",
             "value": "per-node endpoint override",
             "reason": "Would pin the suite to one vendor's URL shape. The two wire "
                       "dialects are resolved by LlmEndpointGuard and asserted there.",
             "coveredBy": "tests/NodePilot.Ai.Tests"},
            {"id": "llmQuery.apiKey.override", "dimension": "llmQuery.apiKey",
             "value": "per-node key",
             "reason": "A key in workflow JSON is exactly what the product tells users "
                       "not to do; the suite will not model it.",
             "coveredBy": "tests/NodePilot.Engine.Tests/Activities/LlmQueryActivityTests.cs"},
        ])


def workflows():
    return [rest_api_workflow(), sql_workflow(), sql_integration_workflow(),
            email_workflow(), llm_workflow()]
