"""Cross-cutting coverage that belongs to no single activity: the edge-condition
evaluator and the data bus.

Both need a hand-built topology, because the thing under test is the graph itself.
"""

from suitelib import Step, Workflow, node, X_STEP, X_AFTER_TRIGGER
from spec_core import LOCAL, sched, assert_step, ok_return

SEED_SCRIPT = """
$envName  = 'test-env'
$cpuCount = '4'
$hostname = 'WIN-PC01'
$emptyStr = ''
$isDomain = 'true'
$falseVal = 'false'
Write-Output 'seeded'
"""


def _var(name):
    return {"kind": "variable", "stepId": "seed", "field": "param", "paramName": name}


def _cmp(param, op, value=None):
    out = {"type": "comparison", "left": _var(param), "op": op}
    if value is not None:
        out["right"] = {"kind": "literal", "value": value}
    return out


# Each entry becomes one edge out of the seed node. Every condition here is true, so the
# target must run; the assertion downstream reads every one of them back.
EDGE_OPERATORS = [
    ("eq", "==", _cmp("envName", "==", "test-env")),
    ("ne", "!=", _cmp("envName", "!=", "prod")),
    ("lt", "<", _cmp("cpuCount", "<", "8")),
    ("gt", ">", _cmp("cpuCount", ">", "1")),
    ("le", "<=", _cmp("cpuCount", "<=", "4")),
    ("ge", ">=", _cmp("cpuCount", ">=", "4")),
    ("contains", "contains", _cmp("hostname", "contains", "PC")),
    ("startsWith", "startsWith", _cmp("hostname", "startsWith", "WIN")),
    ("endsWith", "endsWith", _cmp("hostname", "endsWith", "01")),
    ("matches", "matches", _cmp("hostname", "matches", r"^WIN-PC\d+$")),
    ("isEmpty", "isEmpty", _cmp("emptyStr", "isEmpty")),
    ("isNotEmpty", "isNotEmpty", _cmp("hostname", "isNotEmpty")),
    ("isTrue", "isTrue", _cmp("isDomain", "isTrue")),
    ("isFalse", "isFalse", _cmp("falseVal", "isFalse")),
    ("andGroup", "group AND",
     {"type": "group", "op": "AND",
      "children": [_cmp("envName", "==", "test-env"), _cmp("cpuCount", ">=", "4")]}),
    ("orGroup", "group OR",
     {"type": "group", "op": "OR",
      "children": [_cmp("envName", "==", "prod"), _cmp("cpuCount", ">=", "4")]}),
    ("notGroup", "not", {"type": "not", "child": _cmp("hostname", "contains", "PANIC")}),
]


def edge_conditions_workflow():
    nodes, edges = [], []

    def add(n, x, y, output_var=None, disabled=False):
        n["position"] = {"x": x, "y": y}
        if output_var:
            n["data"]["outputVariable"] = output_var
        if disabled:
            n["data"]["disabled"] = True
        nodes.append(n)

    def edge(src, dst, label="Always", condition=None, expression=None, disabled=False):
        data = {"label": label, "condition": condition or ""}
        if expression is not None:
            data["conditionExpression"] = expression
        if disabled:
            data["disabled"] = True
        edges.append({"id": "e-%s-%s" % (src, dst), "source": src, "target": dst,
                      "type": "labeled", "data": data})

    trg = sched("0 0/5 * * * ? *")
    add(node(trg.id, trg.label, trg.activity, trg.config, 0, 0), 0, 1800)
    add(node("seed", "Seed: operands", "runScript",
             {"engine": "auto", "timeoutSeconds": 20, "script": SEED_SCRIPT}, 0, 0),
        X_AFTER_TRIGGER, 1800, output_var="seed")
    nodes[-1]["data"]["targetMachineId"] = LOCAL
    edge(trg.id, "seed", "Always")

    lane_x = X_AFTER_TRIGGER + X_STEP
    y = 100
    passing = []
    for key, label, expression in EDGE_OPERATORS:
        sid = "op_" + key
        add(node(sid, "Log: " + label, "log",
                 {"level": "info", "message": "edge " + label + " taken"}, 0, 0),
            lane_x, y, output_var=sid)
        edge("seed", sid, label, expression=expression)
        passing.append(sid)
        y += 180

    # Legacy string conditions. `.success` is taken because the seed succeeded; `.failed`
    # must not be, which leaves its target Skipped - only a step-status check can see that.
    add(node("legacy_ok", "Log: legacy .success", "log",
             {"level": "info", "message": "legacy success edge taken"}, 0, 0),
        lane_x, y, output_var="legacy_ok")
    edge("seed", "legacy_ok", "On Success", condition="seed.success")
    passing.append("legacy_ok")
    y += 180

    add(node("legacy_fail", "Log: legacy .failed (never)", "log",
             {"level": "info", "message": "must not run"}, 0, 0), lane_x, y,
        output_var="legacy_fail")
    edge("seed", "legacy_fail", "On Failure", condition="seed.failed")
    y += 180

    add(node("edge_off", "Log: behind a DISABLED edge", "log",
             {"level": "info", "message": "must not run"}, 0, 0), lane_x, y,
        output_var="edge_off")
    edge("seed", "edge_off", "DISABLED edge", disabled=True)
    y += 180

    add(node("node_off", "Log: DISABLED node", "log",
             {"level": "info", "message": "must not run"}, 0, 0), lane_x, y,
        output_var="node_off", disabled=True)
    edge("seed", "node_off", "Always")

    join_x = lane_x + X_STEP
    add(node("join", "Junction: waitAll (18)", "junction", {"mode": "waitAll"}, 0, 0),
        join_x, 1800, output_var="join")
    for sid in passing:
        edge(sid, "join")

    checks = "\n".join(
        "$%s = {{%s.param.message}}\nif ([string]::IsNullOrWhiteSpace($%s)) "
        "{ throw \"edge %s did not run\" }" % (sid, sid, sid, label)
        for (key, label, _e), sid in zip(EDGE_OPERATORS, passing[:-1]))
    checks += ("\n$legacy = {{legacy_ok.param.message}}\n"
               "if ([string]::IsNullOrWhiteSpace($legacy)) { throw 'legacy .success edge did not run' }\n"
               "$assertOk = 'edge-conditions'\n")
    a = assert_step(checks)
    add(node(a.id, a.label, a.activity, a.config, 0, 0), join_x + X_STEP, 1800,
        output_var="assert")
    nodes[-1]["data"]["targetMachineId"] = LOCAL
    edge("join", "assert", "Always")
    r = ok_return("edge-conditions")
    add(node(r.id, r.label, r.activity, r.config, 0, 0), join_x + 2 * X_STEP, 1800,
        output_var="ret")
    edge("assert", "ret", "Always")

    cases = []
    for key, label, _e in EDGE_OPERATORS:
        dim = ("edge.condition.structure" if key.endswith("Group")
               else "edge.condition.operator")
        cases.append(Step("op_" + key, "", "log", {}, cases=[{
            "id": "edge.condition." + key, "dimension": dim, "value": label}]))
    cases.append(Step("legacy_ok", "", "log", {}, cases=[{
        "id": "edge.condition.legacy-success", "dimension": "edge.condition.legacy",
        "value": "stepId.success"}]))
    cases.append(Step("legacy_fail", "", "log", {}, cases=[{
        "id": "edge.condition.legacy-failed", "dimension": "edge.condition.legacy",
        "value": "stepId.failed (not taken)",
        "expectedStepStatus": {"stepId": "legacy_fail", "status": "Skipped"}}]))
    cases.append(Step("edge_off", "", "log", {}, cases=[{
        "id": "edge.disabled", "dimension": "edge.disabled", "value": "true",
        "expectedStepStatus": {"stepId": "edge_off", "status": "Skipped"}}]))
    cases.append(Step("node_off", "", "log", {}, cases=[{
        "id": "node.disabled", "dimension": "node.disabled", "value": "true",
        "expectedStepStatus": {"stepId": "node_off", "status": "Skipped"}}]))

    return Workflow(
        60, "edge-conditions", "[TestSuite] edge conditions",
        "All 14 comparison operators plus group AND, group OR and not, each on its own "
        "edge, together with both legacy string conditions, a disabled edge and a "
        "disabled node. The three branches that must not run are checked by their step "
        "status, because a skipped step leaves no result to reference.",
        "positive", "continuous", "A", cases, max_runtime=90,
        definition={"nodes": nodes, "edges": edges}, trigger=trg)


def variable_resolution_workflow():
    steps = [
        Step("seed", "Seed: every tail", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": "$namedValue = 'from-param'\nWrite-Output 'from-output'\n"},
             target_machine=LOCAL,
             cases=[{"id": "variable.tail.output", "dimension": "variable.tail",
                     "value": "{{step.output}}"},
                    {"id": "variable.tail.param", "dimension": "variable.tail",
                     "value": "{{step.param.x}}"},
                    {"id": "variable.tail.success", "dimension": "variable.tail",
                     "value": "{{step.success}}"}]),
        Step("aliased", "Seed under an outputVariable", "runScript",
             {"engine": "auto", "timeoutSeconds": 20,
              "script": "$aliasProbe = 'via-alias'\nWrite-Output 'aliased'\n"},
             target_machine=LOCAL, output_var="myAlias",
             cases=[{"id": "variable.outputVariable", "dimension": "variable.reference",
                     "value": "outputVariable instead of the node id"}]),
        Step("globalRead", "Read a global variable", "log",
             {"level": "info", "message": "self url = {{globals.NP_TESTSUITE_SELF_URL}}"},
             cases=[{"id": "variable.namespace.globals",
                     "dimension": "variable.namespace", "value": "{{globals.NAME}}"}]),
        Step("embedded", "Embedded templates in one string", "log",
             {"level": "info",
              "message": "out={{seed.output}}|param={{seed.param.namedValue}}|ok={{seed.success}}"},
             cases=[{"id": "variable.embedded", "dimension": "variable.reference",
                     "value": "several templates in one value"}]),
        Step("literal", "Unknown tail stays literal", "log",
             {"level": "info", "message": "tail={{seed.notatail}}"},
             cases=[{"id": "variable.tail.unknown", "dimension": "variable.tail",
                     "value": "unknown tail is left as a literal"}]),
        assert_step("""
$out    = {{seed.output}}
$param  = {{seed.param.namedValue}}
$ok     = {{seed.success}}
$alias  = {{myAlias.param.aliasProbe}}
$glob   = {{globalRead.param.message}}
$emb    = {{embedded.param.message}}
$lit    = {{literal.param.message}}

if ($out -notmatch 'from-output') { throw "{{step.output}} resolved to '$out'" }
if ($param -ne 'from-param')      { throw "{{step.param.x}} resolved to '$param'" }
if ($ok -ne 'true')               { throw "{{step.success}} resolved to '$ok'" }
if ($alias -ne 'via-alias')       { throw "outputVariable reference resolved to '$alias'" }
if ($glob -notmatch 'http')       { throw "globals namespace resolved to '$glob'" }
if ($emb -notmatch 'out=from-output') { throw "embedded output template: '$emb'" }
if ($emb -notmatch 'param=from-param') { throw "embedded param template: '$emb'" }
if ($emb -notmatch 'ok=true')     { throw "embedded success template: '$emb'" }
# Only four tails exist; anything else is deliberately left alone rather than blanked.
if ($lit -notmatch 'notatail')    { throw "an unknown tail should stay literal, got '$lit'" }
$assertOk = 'variable-resolution'
"""),
        ok_return("variable-resolution"),
    ]
    return Workflow(
        62, "variable-resolution", "[TestSuite] variable resolution",
        "Every tail the resolver knows, the globals namespace, an outputVariable alias, "
        "several templates inside one value and the unknown tail that is deliberately "
        "left as literal text. The trigger's own parameters are covered by the trigger "
        "workflows, because a manually started run has none.",
        "positive", "continuous", "A", steps, max_runtime=60,
        requires=["globals:NP_TESTSUITE_SELF_URL"])


def workflows():
    return [edge_conditions_workflow(), variable_resolution_workflow()]
