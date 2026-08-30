"""Generates the NodePilot test suite: one export envelope per workflow plus the
coverage manifest both the guard test and Verify-TestSuite.ps1 read.

    python scripts/test-suite/build_suite.py

Regenerating is idempotent. Edit the spec modules, never the emitted JSON - the JSON is
an artefact and a hand edit is silently overwritten on the next run.
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from suitelib import (TIERS, TIER_INTERVAL_SECONDS, collect_cases, envelope,  # noqa: E402
                      write_json)
from spec_core import sched  # noqa: E402

import spec_engine  # noqa: E402
import spec_controlflow  # noqa: E402
import spec_remote_fs  # noqa: E402
import spec_remote_sys  # noqa: E402
import spec_netlocal  # noqa: E402
import spec_negative  # noqa: E402
import spec_invasive  # noqa: E402
import spec_crosscut  # noqa: E402
import spec_triggers  # noqa: E402
import spec_custom  # noqa: E402

SPEC_MODULES = [spec_engine, spec_controlflow, spec_remote_fs, spec_remote_sys,
                spec_netlocal, spec_crosscut, spec_triggers, spec_custom,
                spec_negative, spec_invasive]

CONTRACT_DIR = {"positive": "positive", "negative": "negative", "invasive": "invasive"}


def assign_crons(workflows):
    """Round-robin the staggered offsets within each tier so the suite does not fire as
    one block. Assignment is by workflow number, so it is stable across regenerations."""
    by_tier = {}
    for wf in sorted(workflows, key=lambda w: w.num):
        if wf.tier is None:
            continue
        by_tier.setdefault(wf.tier, []).append(wf)
    for tier, members in by_tier.items():
        offsets = TIERS[tier]
        for i, wf in enumerate(members):
            wf.cron = offsets[i % len(offsets)]
            wf.trigger = sched(wf.cron)
            if wf.definition is None:
                continue
            # A hand-built topology carries its trigger inside the definition, so the
            # assigned offset has to be written there too or the manifest and the
            # workflow would claim different cadences.
            for n in wf.definition["nodes"]:
                if n["data"]["activityType"] == "scheduleTrigger":
                    n["data"]["config"]["cronExpression"] = wf.cron
                    n["data"]["label"] = "Schedule: %s" % wf.cron


def check_runtime_budget(wf):
    """A workflow that outlives half its cadence stacks up behind
    MaxConcurrentExecutions=1 and is reported as deferred rather than failing loudly."""
    if wf.tier is None:
        return
    budget = TIER_INTERVAL_SECONDS[wf.tier] // 2
    if wf.max_runtime > budget:
        raise SystemExit(
            "%s: maxRuntime %ds exceeds half of the tier-%s cadence (%ds)"
            % (wf.name, wf.max_runtime, wf.tier, budget))


def main():
    workflows = []
    for mod in SPEC_MODULES:
        workflows.extend(mod.workflows())

    names = [w.name for w in workflows]
    dupes = {n for n in names if names.count(n) > 1}
    if dupes:
        raise SystemExit("duplicate workflow names: %s" % ", ".join(sorted(dupes)))

    assign_crons(workflows)

    cases = []
    for wf in sorted(workflows, key=lambda w: w.num):
        check_runtime_budget(wf)
        out = os.path.join(HERE, CONTRACT_DIR[wf.contract], wf.filename)
        write_json(out, envelope(wf))
        cases.extend(collect_cases(wf))

    case_ids = [c["id"] for c in cases]
    dupe_ids = {i for i in case_ids if case_ids.count(i) > 1}
    if dupe_ids:
        raise SystemExit("duplicate case ids: %s" % ", ".join(sorted(dupe_ids)))

    manifest = {
        "schemaVersion": 1,
        "profiles": {
            "continuous": {
                "description": "Runs on every host, no external prerequisite.",
                "cronTiers": ["A", "B"],
            },
            "integration": {
                "description": "Needs an external dependency. Installed but left "
                               "disabled when the prerequisite is absent.",
                "cronTiers": ["C"],
            },
            "invasive": {
                "description": "Mutates the host. Opt-in per machine.",
                "cronTiers": ["C"],
                "requires": ["globals:NP_TESTSUITE_INVASIVE"],
            },
            "excluded": {
                "description": "Deliberately not exercised at runtime. Every entry "
                               "carries a reason and, where one exists, the unit test "
                               "that covers it instead.",
            },
        },
        "workflows": [
            {
                "name": wf.name,
                "file": os.path.join(CONTRACT_DIR[wf.contract], wf.filename).replace("\\", "/"),
                "contract": wf.contract,
                "profile": wf.profile,
                "tier": wf.tier,
                "cron": getattr(wf, "cron", None),
                "maxRuntimeSeconds": wf.max_runtime,
                "judgeBy": wf.judge_by,
                "requires": wf.requires,
            }
            for wf in sorted(workflows, key=lambda w: w.num)
        ],
        "cases": cases,
    }
    write_json(os.path.join(HERE, "suite-manifest.json"), manifest)

    # The custom-activity definition is not a workflow, so it cannot ride in an export
    # envelope. The installer creates it, enables it as Admin and substitutes the id it
    # gets back into the placeholder the generated node carries.
    write_json(os.path.join(HERE, "custom-activity-definition.json"), spec_custom.DEFINITION)

    counts = {}
    for c in cases:
        counts[c["expectedOutcome"]] = counts.get(c["expectedOutcome"], 0) + 1
    print("workflows: %d" % len(workflows))
    print("cases:     %d (%s)" % (
        len(cases), ", ".join("%s=%d" % kv for kv in sorted(counts.items()))))


if __name__ == "__main__":
    main()
