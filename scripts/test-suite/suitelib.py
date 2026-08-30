"""Shared model and emitter for the NodePilot test suite.

The suite is generated rather than hand-authored so that layout, cron tiers and the
coverage manifest cannot drift apart. `build_suite.py` holds the specs; this module
turns them into export envelopes plus `suite-manifest.json`.

Layout follows docs/workflow-styleguide.md: linear snake, 320 px x-step, 380 px after
the trigger octagon, 240 px row pitch, every position on a multiple of 20.
"""

import json
import os

SCHEMA = "nodepilot-workflow-export/v1"
EXPORT_VERSION = 1
EXPORTED_AT = "2026-08-30T00:00:00Z"

# Sandbox roots. `runtime` holds the long-lived trigger fixtures and is never cleaned by a
# run; only `runs/<cid>` is per-execution state.
SANDBOX_ROOT = r"C:\Temp\NP-TestSuite"
RUNTIME_ROOT = SANDBOX_ROOT + r"\runtime"
RUNS_ROOT = SANDBOX_ROOT + r"\runs"
REG_ROOT = r"HKCU:\SOFTWARE\NP-TestSuite"

# Cron tiers. Minute offsets are staggered so the whole suite does not fire on :00.
TIERS = {
    "A": ["0 0/5 * * * ? *", "0 1/5 * * * ? *", "0 2/5 * * * ? *",
          "0 3/5 * * * ? *", "0 4/5 * * * ? *"],
    "B": ["0 5/15 * * * ? *", "0 10/15 * * * ? *"],
    "C": ["0 7/30 * * * ? *", "0 22/30 * * * ? *"],
    "D": ["0 3/10 * * * ? *"],
}
TIER_INTERVAL_SECONDS = {"A": 300, "B": 900, "C": 1800, "D": 600}

X_STEP = 320
X_AFTER_TRIGGER = 380
ROW_PITCH = 240
NODES_PER_ROW = 8


class Step:
    """One node in a workflow, plus the manifest cases it is responsible for."""

    def __init__(self, sid, label, activity, config, cases=None, output_var=None,
                 target_machine=None, condition_from_prev=None):
        self.id = sid
        self.label = label
        self.activity = activity
        self.config = config
        self.cases = cases or []
        self.output_var = output_var or sid
        self.target_machine = target_machine
        # Overrides the default "Always" edge coming into this node.
        self.condition_from_prev = condition_from_prev


class Workflow:
    """A generated suite workflow. `contract` drives which folder it lands in and how
    Verify-TestSuite.ps1 judges it."""

    def __init__(self, num, key, name, description, contract, profile, tier,
                 steps, max_runtime, requires=None, trigger=None, nodes_per_row=None,
                 definition=None, excluded=None, judge_by="run"):
        assert contract in ("positive", "negative", "invasive")
        self.num = num
        self.key = key
        self.name = name
        self.description = description
        self.contract = contract
        self.profile = profile
        self.tier = tier
        self.steps = steps
        self.max_runtime = max_runtime
        self.requires = requires or []
        # Non-schedule roots (the six trigger workflows) pass their own trigger node.
        self.trigger = trigger
        self.nodes_per_row = nodes_per_row or NODES_PER_ROW
        # Prebuilt topology for workflows whose graph shape is the thing under test.
        self.definition = definition
        # Dimensions deliberately not exercised at runtime; each needs a reason and,
        # where one exists, the unit test that covers it instead.
        self.excluded = excluded or []
        # "run" can be started on demand; "cadence" only makes sense when its own source
        # fired it, because a hand-started run carries none of the trigger parameters.
        self.judge_by = judge_by

    @property
    def filename(self):
        return "%02d-%s.json" % (self.num, self.key)


def node(sid, label, activity, config, x, y, output_var=None, target_machine=None,
         disabled=False):
    data = {"label": label, "activityType": activity, "config": config}
    if output_var:
        data["outputVariable"] = output_var
    if target_machine:
        data["targetMachineId"] = target_machine
    if disabled:
        data["disabled"] = True
    return {"id": sid, "type": "activity",
            "position": {"x": x, "y": y}, "data": data}


def _snake_positions(count, nodes_per_row):
    """Positions for a linear chain laid out as a snake. Returns (x, y, bend) per index,
    where `bend` marks the hop into this node as a row change."""
    out = []
    for i in range(count):
        row = i // nodes_per_row
        col = i % nodes_per_row
        if row % 2 == 1:
            col = nodes_per_row - 1 - col
        # Column 0 sits at the origin; the first hop needs extra headroom because the
        # trigger renders as an octagon at 1.55x its bbox. Every row shares the same
        # column grid so the snake stays aligned.
        x = 0 if col == 0 else X_AFTER_TRIGGER + (col - 1) * X_STEP
        out.append((int(x), row * ROW_PITCH + 260, i > 0 and i % nodes_per_row == 0))
    return out


def build_definition(wf):
    """Chain every step with Always edges. Ancestry is what the assertions rely on: a
    linear chain makes every variant an ancestor of the assert node, so `{{v3.param.x}}`
    always resolves. Row changes hand off bottom -> top."""
    chain = ([wf.trigger] if wf.trigger else []) + wf.steps
    positions = _snake_positions(len(chain), wf.nodes_per_row)
    nodes, edges = [], []
    for i, step in enumerate(chain):
        x, y, _ = positions[i]
        nodes.append(node(step.id, step.label, step.activity, step.config, x, y,
                          output_var=step.output_var,
                          target_machine=step.target_machine))
        if i == 0:
            continue
        prev = chain[i - 1]
        _, _, bend = positions[i]
        edge = {"id": "e-%s-%s" % (prev.id, step.id),
                "source": prev.id, "target": step.id, "type": "labeled",
                "data": {"label": "Always", "condition": ""}}
        if step.condition_from_prev is not None:
            edge["data"]["label"] = step.condition_from_prev[0]
            edge["data"]["condition"] = step.condition_from_prev[1]
        if bend:
            edge["sourceHandle"] = "bottom"
            edge["targetHandle"] = "top"
        edges.append(edge)
    return {"nodes": nodes, "edges": edges}


def envelope(wf):
    return {
        "schema": SCHEMA,
        "exportVersion": EXPORT_VERSION,
        "exportedAt": EXPORTED_AT,
        "workflows": [{
            "name": wf.name,
            "description": wf.description,
            "definition": wf.definition or build_definition(wf),
        }],
    }


def collect_cases(wf):
    """Flatten every step's manifest cases, filling in what the step already knows."""
    out = []
    for step in wf.steps:
        for case in step.cases:
            entry = {
                "id": case["id"],
                "dimension": case["dimension"],
                "value": case["value"],
                "profile": case.get("profile", wf.profile),
                "expectedOutcome": case.get("expectedOutcome",
                                            "workflow-failure" if wf.contract == "negative"
                                            else "success"),
                "workflow": wf.name,
                "nodeId": step.id,
            }
            if entry["expectedOutcome"] == "success":
                entry["assertedBy"] = case.get("assertedBy", "assert")
                # Some variants are proven through a different node's result: a create is
                # proven by the following exists, an encoding by the bytes a later script
                # reads back. Naming that node keeps the guard strict instead of having to
                # loosen it.
                if "assertedVia" in case:
                    entry["assertedVia"] = case["assertedVia"]
            if entry["expectedOutcome"] == "workflow-failure":
                entry["expectedFailure"] = case.get(
                    "expectedFailure", {"stepId": step.id, "errorContains": ""})
            if wf.requires or case.get("requires"):
                entry["requires"] = case.get("requires", wf.requires)
            if "expectedStepStatus" in case:
                entry["expectedStepStatus"] = case["expectedStepStatus"]
                entry["assertedBy"] = "verifier:stepStatus"
            if "expectedReturnData" in case:
                entry["expectedReturnData"] = case["expectedReturnData"]
                entry["assertedBy"] = "verifier:returnData"
            entry["maxRuntimeSeconds"] = case.get("maxRuntimeSeconds", wf.max_runtime)
            out.append(entry)
    for ex in wf.excluded:
        entry = dict(ex)
        entry.setdefault("profile", "excluded")
        entry.setdefault("expectedOutcome", "excluded")
        entry["workflow"] = wf.name
        assert entry.get("reason"), "excluded case %s needs a reason" % entry["id"]
        out.append(entry)
    return out


def write_json(path, payload):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(payload, fh, indent=2, ensure_ascii=False)
        fh.write("\n")


def build_definition_custom(nodes, edges):
    """Escape hatch for workflows whose topology is the thing under test (junction modes,
    edge conditions). Everything else uses the linear snake."""
    return {"nodes": nodes, "edges": edges}
