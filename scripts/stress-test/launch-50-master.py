#!/usr/bin/env python3
"""Sync-launcher: 50 parallel executions of Master Test workflow at the exact same instant
   (via threading.Barrier so all threads release simultaneously)."""
import json
import os
import sys
import time
import threading
import statistics
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime

BASE_URL = "http://localhost:5000"
USER = "admin"
PASSWORD = "admin123"
WORKFLOW_NAME = os.environ.get("NODEPILOT_STRESS_WORKFLOW", "Muster — Alle Aktivitäten")
PARALLEL = int(os.environ.get("NODEPILOT_STRESS_PARALLEL", "100"))
LABEL_PREFIX = os.environ.get("NODEPILOT_STRESS_LABEL_PREFIX", "50x-sync")
RUN_LABEL = f"{LABEL_PREFIX}-{time.strftime('%Y%m%d-%H%M%S')}"


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}.{int(time.time()*1000)%1000:03d}] {msg}", flush=True)


def http_json(method: str, path: str, token: str | None = None, body=None, timeout: int = 30, extra_headers=None):
    url = f"{BASE_URL}{path}"
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if extra_headers:
        headers.update(extra_headers)
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
    log(f"Login {USER} @ {BASE_URL}")
    # Auth H-5 migration: the JWT now lives in an httpOnly np_auth cookie and the login
    # body returns identity only (userId/username/role). Bearer clients like this stress
    # harness must opt in via the X-Auth-Token-Response header to also get the raw token
    # in the body (same mechanism the np CLI uses). Without it, resp has no "token" key.
    status, resp = http_json("POST", "/api/auth/login",
                             body={"username": USER, "password": PASSWORD},
                             extra_headers={"X-Auth-Token-Response": "true"})
    if status != 200:
        log(f"login failed: {status} {resp}")
        return 1
    token = resp.get("token") if isinstance(resp, dict) else None
    if not token:
        log(f"login ok but no token in body (X-Auth-Token-Response opt-in missing/unsupported?): {resp}")
        return 1

    log(f"Lookup workflow '{WORKFLOW_NAME}'")
    _, wfs = http_json("GET", "/api/workflows", token=token)
    target = next((w for w in wfs if w["name"] == WORKFLOW_NAME), None)
    if not target:
        log(f"workflow not found: {WORKFLOW_NAME}")
        return 1
    wf_id = target["id"]
    log(f"Workflow id={wf_id} (enabled={target.get('isEnabled')})")
    log(f"Run label prefix={RUN_LABEL}")

    # Pre-build everything so the only thing happening after the barrier-release is the
    # actual urlopen() call. Keeps wall-time variance to a minimum.
    barrier = threading.Barrier(PARALLEL + 1)  # +1 for the main thread
    fire_times = [None] * PARALLEL
    results = [None] * PARALLEL

    def fire(idx: int):
        url = f"{BASE_URL}/api/workflows/{wf_id}/execute"
        headers = {"Content-Type": "application/json", "Authorization": f"Bearer {token}"}
        data = json.dumps({"parameters": {"label": f"{RUN_LABEL}-{idx}"}}).encode("utf-8")
        req = urllib.request.Request(url, data=data, headers=headers, method="POST")
        barrier.wait()  # block until ALL 50 threads + main are at the gate
        t0 = time.perf_counter_ns()
        fire_times[idx] = t0
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                raw = resp.read()
                ms = (time.perf_counter_ns() - t0) / 1_000_000
                body = json.loads(raw) if raw else None
                results[idx] = (resp.status, body, ms)
        except urllib.error.HTTPError as e:
            ms = (time.perf_counter_ns() - t0) / 1_000_000
            results[idx] = (e.code, e.read().decode("utf-8", errors="replace"), ms)
        except Exception as e:
            ms = (time.perf_counter_ns() - t0) / 1_000_000
            results[idx] = (-1, str(e), ms)

    log(f"Firing {PARALLEL} parallel executions (barrier-synced)")
    with ThreadPoolExecutor(max_workers=PARALLEL) as pool:
        futures = [pool.submit(fire, i) for i in range(PARALLEL)]
        # Give threads ~250ms to spin up and reach the barrier
        time.sleep(0.25)
        wall_t0 = time.perf_counter_ns()
        barrier.wait()  # release everyone
        for f in as_completed(futures):
            f.result()
        wall_ms = (time.perf_counter_ns() - wall_t0) / 1_000_000

    # How tightly did the 50 fires happen?
    valid_fires = [t for t in fire_times if t is not None]
    fire_spread_us = (max(valid_fires) - min(valid_fires)) / 1_000 if valid_fires else 0

    accepted = [r for r in results if r and r[0] in (200, 202)]
    failed = [r for r in results if r and r[0] not in (200, 202)]
    log(f"Launched: accepted={len(accepted)} failed={len(failed)} "
        f"launch-wall={wall_ms:.1f}ms fire-spread={fire_spread_us:.1f}us")

    if failed:
        for r in failed[:5]:
            log(f"  FAIL status={r[0]} resp={str(r[1])[:120]}")

    accept_latencies = [r[2] for r in accepted]
    if accept_latencies:
        log(f"POST /execute latency-ms: min={min(accept_latencies):.0f} "
            f"p50={statistics.median(accept_latencies):.0f} "
            f"p95={statistics.quantiles(accept_latencies, n=20)[18] if len(accept_latencies) >= 20 else max(accept_latencies):.0f} "
            f"max={max(accept_latencies):.0f}")

    exec_ids = []
    for r in accepted:
        if isinstance(r[1], dict):
            exec_ids.append(r[1].get("id") or r[1].get("executionId"))
    exec_ids = [e for e in exec_ids if e]
    if not exec_ids:
        log("No executions accepted -- aborting")
        return 1

    log(f"Polling {len(exec_ids)} executions")
    status_map = {eid: "Unknown" for eid in exec_ids}
    terminal = {"Succeeded", "Failed", "Cancelled"}
    poll_t0 = time.monotonic()
    deadline = poll_t0 + 600
    last_log = 0.0

    while time.monotonic() < deadline:
        pending = [k for k, v in status_map.items() if v not in terminal]
        if not pending:
            break
        try:
            for eid in pending:
                _, item = http_json("GET", f"/api/executions/{eid}", token=token, timeout=10)
                if isinstance(item, dict) and item.get("id") in status_map:
                    status_map[item["id"]] = item["status"]
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

    counts = {}
    for v in status_map.values():
        counts[v] = counts.get(v, 0) + 1
    log("=== FINAL STATUS ===")
    for k, v in sorted(counts.items()):
        log(f"  {k}: {v}")
    log(f"poll-wall={time.monotonic() - poll_t0:.1f}s executions={len(exec_ids)}")

    # Fetch per-exec durations + step counts
    durations = []
    step_counts = []
    for eid in exec_ids:
        try:
            _, d = http_json("GET", f"/api/executions/{eid}", token=token, timeout=10)
            if isinstance(d, dict) and d.get("startedAt") and d.get("completedAt"):
                def parse(s):
                    s = s.replace("Z", "+00:00")
                    return datetime.fromisoformat(s)
                durations.append((parse(d["completedAt"]) - parse(d["startedAt"])).total_seconds())
            _, steps = http_json("GET", f"/api/executions/{eid}/steps", token=token, timeout=10)
            if isinstance(steps, list):
                step_counts.append(len(steps))
        except Exception:
            pass
    if durations:
        durations.sort()
        n = len(durations)
        p50 = durations[n // 2]
        p95 = durations[min(int(n * 0.95), n - 1)]
        log(f"per-execution sec: min={durations[0]:.1f} p50={p50:.1f} "
            f"p95={p95:.1f} max={durations[-1]:.1f} (n={n})")
    if step_counts:
        log(f"per-execution steps: avg={statistics.mean(step_counts):.0f} "
            f"min={min(step_counts)} max={max(step_counts)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
