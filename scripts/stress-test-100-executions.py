#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Stress test: Launch 100 workflow executions in ~30 seconds and measure performance.
"""

import requests
import json
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from statistics import mean, stdev, median
from datetime import datetime
import sys
import io

# Force UTF-8 output
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

BASE_URL = "http://localhost:5000"
WORKFLOW_NAME = "Stress Test Simple"
NUM_EXECUTIONS = 100
MAX_WORKERS = 10  # Concurrent requests

# Global token cache
_auth_token = None
_token_lock = __import__('threading').Lock()

def get_auth_token():
    """Get or refresh Bearer token with caching."""
    global _auth_token

    if _auth_token:
        return _auth_token

    with _token_lock:
        # Double-check pattern
        if _auth_token:
            return _auth_token

        login_url = f"{BASE_URL}/api/auth/login"
        login_payload = {
            "username": "admin",
            "password": "admin123"
        }
        try:
            resp = requests.post(login_url, json=login_payload, timeout=5)
            if resp.status_code == 200:
                data = resp.json()
                _auth_token = data.get("token")
                if _auth_token:
                    print(f"[✓] Auth token obtained")
                    return _auth_token
        except Exception as e:
            print(f"[WARN] Auth failed: {e}")
    return None

def create_auth_header():
    """Return auth header with cached token."""
    token = get_auth_token()
    if token:
        return {"Authorization": f"Bearer {token}"}
    return {}

def import_test_workflow():
    """Create a simple test workflow if not already present."""
    print(f"[*] Creating test workflow '{WORKFLOW_NAME}'...")

    # Simple workflow with just a delay
    workflow_def = {
        "nodes": [
            {
                "id": "delay",
                "type": "activity",
                "position": {"x": 100, "y": 100},
                "data": {
                    "label": "Delay 1s",
                    "activityType": "delay",
                    "config": {"seconds": 1}
                }
            }
        ],
        "edges": []
    }

    create_url = f"{BASE_URL}/api/workflows"
    payload = {
        "name": WORKFLOW_NAME,
        "description": "Simple stress test workflow",
        "definitionJson": json.dumps(workflow_def)
    }

    headers = create_auth_header()
    try:
        resp = requests.post(create_url, json=payload, headers=headers, timeout=10)
        if resp.status_code in [200, 201]:
            print(f"[✓] Workflow created successfully")
            return True
        else:
            print(f"[WARN] Create returned {resp.status_code}")
            return False
    except Exception as e:
        print(f"[WARN] Create failed: {e}")
        return False

def get_workflow_id():
    """Get the workflow ID by name."""
    print(f"[*] Looking up workflow ID for '{WORKFLOW_NAME}'...")
    list_url = f"{BASE_URL}/api/workflows"
    headers = create_auth_header()
    try:
        resp = requests.get(list_url, headers=headers, timeout=5)
        if resp.status_code == 200:
            workflows = resp.json()
            for wf in workflows:
                if wf.get("name") == WORKFLOW_NAME:
                    wid = wf.get("id")
                    print(f"[✓] Found workflow ID: {wid}")
                    return wid
        print(f"[!] Workflow '{WORKFLOW_NAME}' not found in list")
    except Exception as e:
        print(f"[WARN] Lookup failed: {e}")
    return None

def execute_workflow(workflow_id, attempt_num):
    """Execute a single workflow and record metrics."""
    execute_url = f"{BASE_URL}/api/workflows/{workflow_id}/execute"
    payload = {
        "parameters": {},
        "timeoutSeconds": 300,
        "debug": False
    }
    headers = create_auth_header()

    start = time.time()
    try:
        resp = requests.post(execute_url, json=payload, headers=headers, timeout=30)
        elapsed = time.time() - start
        status = resp.status_code
        execution_id = None

        if status == 202:
            try:
                data = resp.json()
                execution_id = data.get("executionId")
            except:
                pass

        return {
            "attempt": attempt_num,
            "status": status,
            "elapsed_ms": elapsed * 1000,
            "execution_id": execution_id,
            "success": status == 202
        }
    except Exception as e:
        elapsed = time.time() - start
        return {
            "attempt": attempt_num,
            "status": 0,
            "elapsed_ms": elapsed * 1000,
            "execution_id": None,
            "success": False,
            "error": str(e)
        }

def main():
    global _auth_token

    print("=" * 70)
    print("NODEPILOT STRESS TEST: 100 Executions in 30 Seconds")
    print("=" * 70)
    print(f"[*] Start time: {datetime.now().isoformat()}")
    print(f"[*] Target: {NUM_EXECUTIONS} executions with {MAX_WORKERS} concurrent workers")
    print()

    # Step 0: Get auth token first
    _auth_token = get_auth_token()
    if not _auth_token:
        print("[!] ERROR: Could not obtain auth token. Aborting.")
        sys.exit(1)
    print()

    # Step 1: Import workflow
    import_test_workflow()
    time.sleep(1)

    # Step 2: Get workflow ID
    workflow_id = get_workflow_id()
    if not workflow_id:
        print("[!] ERROR: Could not find or import workflow. Aborting.")
        sys.exit(1)

    print()
    print(f"[*] Starting execution burst at {datetime.now().isoformat()}...")
    print()

    # Step 3: Launch execution burst
    results = []
    start_time = time.time()

    with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
        futures = []
        for i in range(NUM_EXECUTIONS):
            future = executor.submit(execute_workflow, workflow_id, i + 1)
            futures.append(future)

        completed = 0
        for future in as_completed(futures):
            result = future.result()
            results.append(result)
            completed += 1
            if completed % 10 == 0:
                print(f"[+] {completed}/{NUM_EXECUTIONS} executions launched")

    total_time = time.time() - start_time

    print()
    print("=" * 70)
    print("PERFORMANCE METRICS")
    print("=" * 70)
    print()

    # Parse results
    successful = [r for r in results if r["success"]]
    failed = [r for r in results if not r["success"]]
    response_times = [r["elapsed_ms"] for r in successful]

    print(f"Total Time:        {total_time:.2f} seconds")
    print(f"Successful:        {len(successful)}/{NUM_EXECUTIONS} ({100*len(successful)/NUM_EXECUTIONS:.1f}%)")
    print(f"Failed:            {len(failed)}/{NUM_EXECUTIONS}")
    print()

    if response_times:
        print("Response Time (ms):")
        print(f"  Min:             {min(response_times):.1f}")
        print(f"  Max:             {max(response_times):.1f}")
        print(f"  Mean:            {mean(response_times):.1f}")
        print(f"  Median:          {median(response_times):.1f}")
        if len(response_times) > 1:
            print(f"  Stdev:           {stdev(response_times):.1f}")
        print()

    # Status code distribution
    status_counts = {}
    for r in results:
        status = r["status"]
        status_counts[status] = status_counts.get(status, 0) + 1

    print("Status Code Distribution:")
    for status in sorted(status_counts.keys()):
        count = status_counts[status]
        pct = 100 * count / NUM_EXECUTIONS
        print(f"  {status:3d}: {count:3d} ({pct:5.1f}%)")
    print()

    # Throughput
    throughput = NUM_EXECUTIONS / total_time
    print(f"Throughput:        {throughput:.1f} executions/second")
    print()

    # Sample execution IDs
    exec_ids = [r["execution_id"] for r in successful[:5] if r["execution_id"]]
    if exec_ids:
        print("Sample Execution IDs (first 5):")
        for eid in exec_ids:
            print(f"  - {eid}")
        print()

    print("=" * 70)
    print(f"[*] End time: {datetime.now().isoformat()}")
    print("=" * 70)

if __name__ == "__main__":
    main()
