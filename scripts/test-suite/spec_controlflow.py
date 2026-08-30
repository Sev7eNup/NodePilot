"""Control-flow coverage: decision (all 14 operators plus the group/not structures),
junction (all three modes), forEach, startWorkflow and returnData."""

from suitelib import Step, Workflow, node, X_STEP, X_AFTER_TRIGGER
from spec_core import LOCAL, sched, assert_step, ok_return

SEED_SCRIPT = """
$envName   = 'test-env'
$cpuCount  = '4'
$hostname  = 'WIN-PC01'
$emptyStr  = ''
$isDomain  = 'true'
$falseVal  = 'false'
Write-Output 'seeded'
"""


def _var(name):
    return {"kind": "variable", "stepId": "seed", "field": "param", "paramName": name}


def _lit(value):
    return {"kind": "literal", "value": value}


def _cmp(param, op, value=None):
    out = {"type": "comparison", "left": _var(param), "op": op}
    if value is not None:
        out["right"] = _lit(value)
    return out


# (node id, case name, condition, expected param.case). The expected value is what makes
# these real assertions: a decision that silently falls through to "default" would
# otherwise look identical to a matching one.
DECISION_CASES = [
    ("d01", "eq", _cmp("envName", "==", "test-env"), "eq"),
    ("d02", "ne", _cmp("envName", "!=", "prod"), "ne"),
    ("d03", "lt", _cmp("cpuCount", "<", "8"), "lt"),
    ("d04", "gt", _cmp("cpuCount", ">", "1"), "gt"),
    ("d05", "le", _cmp("cpuCount", "<=", "4"), "le"),
    ("d06", "ge", _cmp("cpuCount", ">=", "4"), "ge"),
    ("d07", "contains", _cmp("hostname", "contains", "PC"), "contains"),
    ("d08", "startsWith", _cmp("hostname", "startsWith", "WIN"), "startsWith"),
    ("d09", "endsWith", _cmp("hostname", "endsWith", "01"), "endsWith"),
    ("d10", "matches", _cmp("hostname", "matches", r"^WIN-PC\d+$"), "matches"),
    ("d11", "isEmpty", _cmp("emptyStr", "isEmpty"), "isEmpty"),
    ("d12", "isNotEmpty", _cmp("hostname", "isNotEmpty"), "isNotEmpty"),
    ("d13", "isTrue", _cmp("isDomain", "isTrue"), "isTrue"),
    ("d14", "isFalse", _cmp("falseVal", "isFalse"), "isFalse"),
    ("d15", "andGroup",
     {"type": "group", "op": "AND",
      "children": [_cmp("envName", "==", "test-env"), _cmp("cpuCount", ">=", "4")]},
     "andGroup"),
    ("d16", "orGroup",
     {"type": "group", "op": "OR",
      "children": [_cmp("envName", "==", "prod"), _cmp("cpuCount", ">=", "4")]},
     "orGroup"),
    ("d17", "notGroup",
     {"type": "not", "child": _cmp("hostname", "contains", "PANIC")},
     "notGroup"),
    # No case can match, so the executor must fall back to defaultCaseName.
    ("d18", "neverMatches", _cmp("envName", "==", "no-such-env"), "default"),
]

OPERATOR_DIMENSION = {
    "eq": "==", "ne": "!=", "lt": "<", "gt": ">", "le": "<=", "ge": ">=",
    "contains": "contains", "startsWith": "startsWith", "endsWith": "endsWith",
    "matches": "matches", "isEmpty": "isEmpty", "isNotEmpty": "isNotEmpty",
    "isTrue": "isTrue", "isFalse": "isFalse",
}


def decision_workflow():
    steps = [Step("seed", "Seed: operands", "runScript",
                  {"engine": "auto", "timeoutSeconds": 20, "script": SEED_SCRIPT},
                  target_machine=LOCAL)]
    for sid, case_name, condition, _expected in DECISION_CASES:
        if case_name in OPERATOR_DIMENSION:
            case = {"id": "decision.operator." + case_name,
                    "dimension": "decision.operator",
                    "value": OPERATOR_DIMENSION[case_name]}
        elif case_name == "neverMatches":
            case = {"id": "decision.defaultCase", "dimension": "decision.cases",
                    "value": "no match -> defaultCaseName"}
        else:
            case = {"id": "decision.structure." + case_name,
                    "dimension": "decision.condition.type",
                    "value": {"andGroup": "group AND", "orGroup": "group OR",
                              "notGroup": "not"}[case_name]}
        steps.append(Step(sid, "decision: " + case_name, "decision",
                          {"cases": [{"name": case_name, "condition": condition}],
                           "defaultCaseName": "default"},
                          cases=[case]))
    # Two cases that are both true; the executor must take the first.
    steps.append(Step("d19", "decision: first match wins", "decision",
                      {"cases": [{"name": "winner", "condition": _cmp("cpuCount", ">=", "1")},
                                 {"name": "loser", "condition": _cmp("cpuCount", ">=", "2")}],
                       "defaultCaseName": "default"},
                      cases=[{"id": "decision.cases.first-match-wins",
                              "dimension": "decision.cases", "value": "first match wins"}]))

    checks = "\n".join(
        "$%s = {{%s.param.case}}\nif ($%s -ne '%s') { throw \"decision %s: expected %s, got '$%s'\" }"
        % (sid, sid, sid, expected, case_name, expected, sid)
        for sid, case_name, _c, expected in DECISION_CASES)
    checks += ("\n$d19 = {{d19.param.case}}\n"
               "if ($d19 -ne 'winner') { throw \"decision first-match-wins: got '$d19'\" }\n"
               "$matched = {{d01.param.matched}}\n"
               "if ($matched -ne 'true') { throw \"decision matched flag: got '$matched'\" }\n"
               "$assertOk = 'decision'\n")
    steps.append(assert_step(checks))
    steps.append(ok_return("decision"))
    return Workflow(
        20, "decision", "[TestSuite] decision",
        "All 14 comparison operators, group AND, group OR, not, the default-case "
        "fallthrough and first-match-wins ordering.",
        "positive", "continuous", "A", steps, max_runtime=60, nodes_per_row=7)


def _branch(sid, label, param_name, delay_seconds):
    """A branch that leaves a uniquely named output parameter behind, so a junction
    downstream can be asserted on which upstream results it actually merged."""
    script = ("Start-Sleep -Seconds %d\n$%s = 'reached'\nWrite-Output '%s'\n"
              % (delay_seconds, param_name, param_name))
    return node(sid, label, "runScript",
                {"engine": "auto", "timeoutSeconds": 60, "script": script}, 0, 0)


def junction_workflow():
    """Topology is the thing under test here, so this one is hand-built rather than laid
    out as the usual linear snake."""
    nodes, edges = [], []

    def add(n, x, y, output_var=None):
        n["position"] = {"x": x, "y": y}
        if output_var:
            n["data"]["outputVariable"] = output_var
        nodes.append(n)
        return n

    def edge(src, dst, label="Always", condition=""):
        edges.append({"id": "e-%s-%s" % (src, dst), "source": src, "target": dst,
                      "type": "labeled",
                      "data": {"label": label, "condition": condition}})

    trg = sched("0 0/5 * * * ? *")
    add(node(trg.id, trg.label, trg.activity, trg.config, 0, 0), 0, 700)

    # waitAll over three branches: every upstream parameter must be visible downstream.
    for i, (sid, y) in enumerate([("a1", 260), ("a2", 700), ("a3", 1140)]):
        n = _branch(sid, "branch A%d" % (i + 1), "branchA%d" % (i + 1), 0)
        n["data"]["targetMachineId"] = LOCAL
        add(n, X_AFTER_TRIGGER, y, output_var=sid)
        edge(trg.id, sid)
    add(node("jAll", "Junction: waitAll (3)", "junction", {"mode": "waitAll"}, 0, 0),
        X_AFTER_TRIGGER + X_STEP, 700, output_var="jAll")
    for sid in ("a1", "a2", "a3"):
        edge(sid, "jAll")

    # waitAny: only the fastest branch is guaranteed to have produced a result, so the
    # assertion below never references the slower two.
    for i, (sid, y, secs) in enumerate([("b1", 260, 0), ("b2", 700, 4), ("b3", 1140, 6)]):
        n = _branch(sid, "branch B%d (%ds)" % (i + 1, secs), "branchB%d" % (i + 1), secs)
        n["data"]["targetMachineId"] = LOCAL
        add(n, X_AFTER_TRIGGER + 2 * X_STEP, y, output_var=sid)
        edge("jAll", sid)
    add(node("jAny", "Junction: waitAny (1 of 3)", "junction", {"mode": "waitAny"}, 0, 0),
        X_AFTER_TRIGGER + 3 * X_STEP, 700, output_var="jAny")
    for sid in ("b1", "b2", "b3"):
        edge(sid, "jAny")

    # waitNofM with requiredCount 2. The key is `requiredCount`; an `n` here would be
    # ignored and silently degrade the junction to waitAny.
    for i, (sid, y, secs) in enumerate([("c1", 260, 0), ("c2", 700, 1), ("c3", 1140, 6)]):
        n = _branch(sid, "branch C%d (%ds)" % (i + 1, secs), "branchC%d" % (i + 1), secs)
        n["data"]["targetMachineId"] = LOCAL
        add(n, X_AFTER_TRIGGER + 4 * X_STEP, y, output_var=sid)
        edge("jAny", sid)
    add(node("jNofM", "Junction: waitNofM (2/3)", "junction",
             {"mode": "waitNofM", "requiredCount": 2}, 0, 0),
        X_AFTER_TRIGGER + 5 * X_STEP, 700, output_var="jNofM")
    for sid in ("c1", "c2", "c3"):
        edge(sid, "jNofM")

    assert_body = """
# The junction merges its upstream output parameters, so reading them off the junction
# proves both that the branches ran and that the merge happened.
$a1 = {{jAll.param.branchA1}}
$a2 = {{jAll.param.branchA2}}
$a3 = {{jAll.param.branchA3}}
if ($a1 -ne 'reached' -or $a2 -ne 'reached' -or $a3 -ne 'reached') {
  throw "waitAll did not merge all three branches: '$a1' '$a2' '$a3'"
}
$b1 = {{b1.param.branchB1}}
if ($b1 -ne 'reached') { throw "waitAny: fastest branch did not complete" }
$c1 = {{c1.param.branchC1}}
$c2 = {{c2.param.branchC2}}
if ($c1 -ne 'reached' -or $c2 -ne 'reached') {
  throw "waitNofM(2/3): the two fastest branches should both have run"
}
$assertOk = 'junction'
"""
    a = assert_step(assert_body)
    add(node(a.id, a.label, a.activity, a.config, 0, 0),
        X_AFTER_TRIGGER + 6 * X_STEP, 700, output_var="assert")
    nodes[-1]["data"]["targetMachineId"] = LOCAL
    edge("jNofM", "assert")
    r = ok_return("junction")
    add(node(r.id, r.label, r.activity, r.config, 0, 0),
        X_AFTER_TRIGGER + 7 * X_STEP, 700, output_var="ret")
    edge("assert", "ret")

    steps = [
        Step("jAll", "", "junction", {},
             cases=[{"id": "junction.mode.waitAll", "dimension": "junction.mode",
                     "value": "waitAll"}]),
        Step("jAny", "", "junction", {},
             cases=[{"id": "junction.mode.waitAny", "assertedVia": "b1",
                     "dimension": "junction.mode", "value": "waitAny"}]),
        Step("jNofM", "", "junction", {},
             cases=[{"id": "junction.mode.waitNofM", "assertedVia": "c1",
                     "dimension": "junction.mode", "value": "waitNofM"},
                    {"id": "junction.requiredCount.two", "assertedVia": "c2",
                     "dimension": "junction.requiredCount", "value": "2"}]),
    ]
    return Workflow(
        21, "junction", "[TestSuite] junction",
        "All three junction modes over real parallel branches, asserted through the "
        "output parameters the junction merges.",
        "positive", "continuous", "A", steps, max_runtime=120,
        definition={"nodes": nodes, "edges": edges}, trigger=trg)


CHILD = "[TestSuite] Child: Echo Item"


def for_each_workflow():
    steps = [
        Step("v0", "forEach: json, parallelism 1", "forEach",
             {"items": '["a","b","c"]', "itemsFormat": "json",
              "childWorkflowNameOrId": CHILD, "itemParameterName": "item",
              "indexParameterName": "index", "maxParallelism": 1,
              "continueOnError": False, "timeoutSecondsPerItem": 60},
             cases=[{"id": "forEach.itemsFormat.json", "dimension": "forEach.itemsFormat",
                     "value": "json"},
                    {"id": "forEach.maxParallelism.serial",
                     "dimension": "forEach.maxParallelism", "value": "1"}]),
        Step("v1", "forEach: lines, parallelism 4", "forEach",
             {"items": "one\ntwo\nthree\nfour", "itemsFormat": "lines",
              "childWorkflowNameOrId": CHILD, "maxParallelism": 4,
              "continueOnError": False, "timeoutSecondsPerItem": 60},
             cases=[{"id": "forEach.itemsFormat.lines",
                     "dimension": "forEach.itemsFormat", "value": "lines"},
                    {"id": "forEach.maxParallelism.parallel",
                     "dimension": "forEach.maxParallelism", "value": "4"}]),
        Step("v2", "forEach: auto detects JSON", "forEach",
             {"items": '["x","y"]', "itemsFormat": "auto",
              "childWorkflowNameOrId": CHILD, "maxParallelism": 2,
              "timeoutSecondsPerItem": 60},
             cases=[{"id": "forEach.itemsFormat.auto-json",
                     "dimension": "forEach.itemsFormat", "value": "auto (JSON)"}]),
        Step("v3", "forEach: auto detects lines", "forEach",
             {"items": "p\nq\nr", "itemsFormat": "auto",
              "childWorkflowNameOrId": CHILD, "maxParallelism": 2,
              "timeoutSecondsPerItem": 60},
             cases=[{"id": "forEach.itemsFormat.auto-lines",
                     "dimension": "forEach.itemsFormat", "value": "auto (lines)"}]),
        Step("v4", "forEach: empty collection", "forEach",
             {"items": "[]", "itemsFormat": "json", "childWorkflowNameOrId": CHILD,
              "maxParallelism": 1, "timeoutSecondsPerItem": 60},
             cases=[{"id": "forEach.items.empty", "dimension": "forEach.items",
                     "value": "empty -> 0 iterations"}]),
        Step("v5", "forEach: custom names + static params", "forEach",
             {"items": '["only"]', "itemsFormat": "json",
              "childWorkflowNameOrId": CHILD,
              "itemParameterName": "item", "indexParameterName": "index",
              "parameters": {"extra": "static-value"},
              "maxParallelism": 1, "timeoutSecondsPerItem": 60},
             cases=[{"id": "forEach.parameters.static",
                     "dimension": "forEach.parameters", "value": "static inputs"}]),
        Step("v6", "forEach: continueOnError", "forEach",
             {"items": '["ok1","boom","ok2"]', "itemsFormat": "json",
              "childWorkflowNameOrId": CHILD, "maxParallelism": 1,
              "continueOnError": True, "timeoutSecondsPerItem": 60},
             cases=[{"id": "forEach.continueOnError.true",
                     "dimension": "forEach.continueOnError", "value": "true"}]),
        # Every template is read into a variable before it is used. The runScript template
        # guard parses the script with a bare placeholder in place of each {{...}}, so a
        # template in an expression position such as [int]{{x}} is rejected outright.
        assert_step("""
$t0 = {{v0.param.total}}
$s0 = {{v0.param.succeeded}}
$t1 = {{v1.param.total}}
$s1 = {{v1.param.succeeded}}
$t2 = {{v2.param.total}}
$t3 = {{v3.param.total}}
$t4 = {{v4.param.total}}
$s5 = {{v5.param.succeeded}}
$t6 = {{v6.param.total}}
$f6 = {{v6.param.failed}}
$s6 = {{v6.param.succeeded}}

if ([int]$t0 -ne 3 -or [int]$s0 -ne 3) { throw "forEach json: total/succeeded were $t0/$s0" }
if ([int]$t1 -ne 4 -or [int]$s1 -ne 4) { throw "forEach lines: total/succeeded were $t1/$s1" }
if ([int]$t2 -ne 2) { throw "forEach auto-json total: $t2" }
if ([int]$t3 -ne 3) { throw "forEach auto-lines total: $t3" }
if ([int]$t4 -ne 0) { throw "forEach empty should iterate 0 times, got $t4" }
if ([int]$s5 -ne 1) { throw "forEach static params: child did not succeed" }
if ([int]$t6 -ne 3) { throw "forEach continueOnError total: $t6" }
if ([int]$f6 -ne 1) { throw "forEach continueOnError should record exactly one failed item, got $f6" }
if ([int]$s6 -ne 2) { throw "forEach continueOnError should still run the items after the failure, got $s6" }
$assertOk = 'forEach'
"""),
        ok_return("forEach"),
    ]
    return Workflow(
        22, "forEach", "[TestSuite] forEach",
        "All three item formats, serial and parallel fan-out, the empty collection, "
        "static child parameters and continueOnError with a deliberately failing item.",
        "positive", "continuous", "A", steps, max_runtime=120)


def start_workflow_workflow():
    steps = [
        Step("v0", "startWf: wait + capture", "startWorkflow",
             {"workflowNameOrId": CHILD, "waitForCompletion": True,
              "parameters": {"item": "sync", "index": "0", "extra": "carried"},
              "timeoutSeconds": 60},
             cases=[{"id": "startWorkflow.waitForCompletion.true",
                     "dimension": "startWorkflow.waitForCompletion", "value": "true"},
                    {"id": "startWorkflow.parameters.roundtrip",
                     "dimension": "startWorkflow.parameters",
                     "value": "mirrored back as param.*"},
                    {"id": "startWorkflow.timeoutSeconds",
                     "dimension": "startWorkflow.timeoutSeconds", "value": "60"}]),
        Step("v1", "startWf: fire and forget", "startWorkflow",
             {"workflowNameOrId": CHILD, "waitForCompletion": False,
              "parameters": {"item": "async", "index": "1"}},
             cases=[{"id": "startWorkflow.waitForCompletion.false",
                     "dimension": "startWorkflow.waitForCompletion", "value": "false"}]),
        assert_step("""
$item   = {{v0.param.item}}
$extra  = {{v0.param.extra}}
$echoed = {{v0.param.echoed}}
$status = {{v0.param.__status}}
$wfName = {{v0.param.__workflowName}}
$asyncId = {{v1.param.__executionId}}

if ($item -ne 'sync')    { throw "startWorkflow parameter round-trip: got '$item'" }
if ($extra -ne 'carried') { throw "startWorkflow extra parameter: got '$extra'" }
if ($echoed -ne 'yes')   { throw "child returnData was not mirrored as param.*" }
if ($status -ne 'Succeeded') { throw "startWorkflow __status: got '$status'" }
if ($wfName -notmatch 'Echo Item') { throw "startWorkflow __workflowName: got '$wfName'" }
if ([string]::IsNullOrWhiteSpace($asyncId)) {
  throw "fire-and-forget should still return an execution id"
}
$assertOk = 'startWorkflow'
"""),
        ok_return("startWorkflow"),
    ]
    return Workflow(
        23, "startWorkflow", "[TestSuite] startWorkflow",
        "Both completion modes, the parameter round-trip through the child's returnData "
        "and the system outputs the activity always emits.",
        "positive", "continuous", "A", steps, max_runtime=90)


def return_data_workflow():
    """returnData is terminal, so nothing downstream can assert it. Verify-TestSuite
    reads the execution's stored returnData and compares it against the manifest."""
    steps = [
        Step("a", "Seed A", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": "$alpha = 'first-source'\nWrite-Output 'a'\n"},
             target_machine=LOCAL),
        Step("b", "Seed B (oversized value)", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": "$beta = 'second-source'\n$blob = 'x' * 9000\nWrite-Output 'b'\n"},
             target_machine=LOCAL),
        Step("ret", "Return: every value shape", "returnData",
             {"data": {
                 "static": "constant",
                 "fromA": "{{a.param.alpha}}",
                 "fromB": "{{b.param.beta}}",
                 "embedded": "a={{a.param.alpha}}|b={{b.param.beta}}",
                 "successFlag": "{{a.success}}",
                 "oversized": "{{b.param.blob}}",
             }},
             cases=[
                 {"id": "returnData.data.static", "dimension": "returnData.data",
                  "value": "static constant",
                  "expectedReturnData": {"static": "constant"}},
                 {"id": "returnData.data.multi-source", "dimension": "returnData.data",
                  "value": "templates from two upstream steps",
                  "expectedReturnData": {"fromA": "first-source",
                                         "fromB": "second-source"}},
                 {"id": "returnData.data.embedded", "dimension": "returnData.data",
                  "value": "embedded template",
                  "expectedReturnData": {"embedded": "a=first-source|b=second-source"}},
                 {"id": "returnData.data.success-tail", "dimension": "returnData.data",
                  "value": "{{step.success}}",
                  "expectedReturnData": {"successFlag": "true"}},
                 {"id": "returnData.data.per-value-truncation",
                  "dimension": "returnData.data", "value": "value over 8 KiB",
                  "expectedReturnData": {"oversized": "*(truncated)"}},
             ]),
    ]
    return Workflow(
        24, "returnData", "[TestSuite] returnData",
        "Static constants, templates from two upstream steps, an embedded template, a "
        "success tail and the per-value truncation at 8 KiB.",
        # The over-limit envelope is a hard step failure by design, so it lives in the
        # negative contract (negative/83-controlflow.json) rather than here.
        "positive", "continuous", "A", steps, max_runtime=45)


def workflows():
    return [decision_workflow(), junction_workflow(), for_each_workflow(),
            start_workflow_workflow(), return_data_workflow()]
