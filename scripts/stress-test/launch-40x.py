#!/usr/bin/env python3
"""Stress-Test launcher: upload workflow, fire N parallel executions, poll, summarize."""
import json
import sys
import time
import threading
import urllib.request
import urllib.error
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE_URL = "http://localhost:5000"
USER = "admin"
PASSWORD = "admin123"
WORKFLOW_NAME = "Stress-Test"
DEFINITION_FILE = Path(__file__).parent / "main.json"
PARALLEL = 40


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}.{int(time.time()*1000)%1000:03d}] {msg}", flush=True)


def http_json(method: str, path: str, token: str | None = None, body=None, timeout: int = 30):
    url = f"{BASE_URL}{path}"
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            return resp.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")
        return e.code, body


def main() -> int:
    # 1) Login
    log(f"Login {USER} @ {BASE_URL}")
    status, resp = http_json("POST", "/api/auth/login",
                             body={"username": USER, "password": PASSWORD})
    if status != 200:
        log(f"login failed: {status} {resp}")
        return 1
    token = resp["token"]

    # 2) Upsert workflow
    log(f"Lookup workflow '{WORKFLOW_NAME}'")
    _, wfs = http_json("GET", "/api/workflows", token=token)
    existing = next((w for w in wfs if w["name"] == WORKFLOW_NAME), None)
    def_text = DEFINITION_FILE.read_text(encoding="utf-8")
    payload = {"name": WORKFLOW_NAME, "description": "Ad-hoc load-test", "definitionJson": def_text}
    if existing:
        log(f"Update existing workflow id={existing['id']}")
        status, wf = http_json("PUT", f"/api/workflows/{existing['id']}", token=token, body=payload)
    else:
        log("Create new workflow")
        status, wf = http_json("POST", "/api/workflows", token=token, body=payload)
    if status not in (200, 201):
        log(f"workflow upsert failed: {status} {wf}")
        return 1
    wf_id = wf["id"]
    log(f"Workflow id={wf_id}")

    # 3) Fire N parallel executions
    log(f"Firing {PARALLEL} parallel executions")
    exec_body = {"parameters": {"label": "40x-parallel"}}

    def fire(idx: int):
        t0 = time.monotonic()
        status, resp = http_json("POST", f"/api/workflows/{wf_id}/execute",
                                 token=token, body=exec_body, timeout=30)
        ms = int((time.monotonic() - t0) * 1000)
        return idx, status, resp, ms

    launch_t0 = time.monotonic()
    results = []
    with ThreadPoolExecutor(max_workers=PARALLEL) as pool:
        futures = [pool.submit(fire, i) for i in range(1, PARALLEL + 1)]
        for f in as_completed(futures):
            results.append(f.result())
    launch_ms = int((time.monotonic() - launch_t0) * 1000)

    accepted = [r for r in results if r[1] in (200, 202)]
    failed = [r for r in results if r[1] not in (200, 202)]
    log(f"Launched: accepted={len(accepted)} failed={len(failed)} launch-wall={launch_ms}ms")
    for f in failed[:5]:
        log(f"  FAIL idx={f[0]} status={f[1]} resp={str(f[2])[:200]}")

    exec_ids = []
    for _, _, resp, _ in accepted:
        if isinstance(resp, dict):
            exec_ids.append(resp.get("id") or resp.get("executionId"))
    exec_ids = [e for e in exec_ids if e]
    if not exec_ids:
        log("No executions accepted -- aborting")
        return 1

    # 4) Poll
    log(f"Polling {len(exec_ids)} executions")
    status_map = {eid: "Unknown" for eid in exec_ids}
    terminal = {"Completed", "Failed", "Cancelled", "TimedOut", "PartialFailure"}
    poll_t0 = time.monotonic()
    deadline = poll_t0 + 300
    last_log = 0.0

    while time.monotonic() < deadline:
        pending = [k for k, v in status_map.items() if v not in terminal]
        if not pending:
            break
        try:
            _, all_exec = http_json("GET", "/api/executions?pageSize=200", token=token, timeout=10)
            items = all_exec["items"] if isinstance(all_exec, dict) and "items" in all_exec else all_exec
            for it in items:
                if it["id"] in status_map:
                    status_map[it["id"]] = it["status"]
        except Exception as e:
            log(f"poll err: {e}")

        now = time.monotonic()
        if now - last_log >= 5:
            counts = {}
            for v in status_map.values():
                counts[v] = counts.get(v, 0) + 1
            summary = " ".join(f"{k}={v}" for k, v in sorted(counts.items()))
            log(f"t+{now - poll_t0:.1f}s  {summary}")
            last_log = now
        time.sleep(1.5)

    # 5) Final + per-execution durations
    counts = {}
    for v in status_map.values():
        counts[v] = counts.get(v, 0) + 1
    log("=== FINAL ===")
    for k, v in sorted(counts.items()):
        log(f"  {k}: {v}")
    log(f"poll-wall={time.monotonic() - poll_t0:.1f}s executions={len(exec_ids)}")

    durations = []
    for eid in exec_ids:
        try:
            _, d = http_json("GET", f"/api/executions/{eid}", token=token, timeout=10)
            if isinstance(d, dict) and d.get("startedAt") and d.get("completedAt"):
                from datetime import datetime
                # Trim trailing Z and microseconds variability
                def parse(s):
                    s = s.replace("Z", "+00:00")
                    return datetime.fromisoformat(s)
                durations.append((parse(d["completedAt"]) - parse(d["startedAt"])).total_seconds())
        except Exception:
            pass
    if durations:
        durations.sort()
        n = len(durations)
        p50 = durations[n // 2]
        p95 = durations[min(int(n * 0.95), n - 1)]
        log(f"per-execution seconds: min={durations[0]:.1f} p50={p50:.1f} p95={p95:.1f} max={durations[-1]:.1f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
