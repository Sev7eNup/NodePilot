#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Disable all workflows.
"""

import requests
import json
import sys
import io

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')

BASE_URL = "http://localhost:5000"

def get_auth_token():
    """Get Bearer token."""
    login_url = f"{BASE_URL}/api/auth/login"
    login_payload = {
        "username": "admin",
        "password": "admin123"
    }
    try:
        resp = requests.post(login_url, json=login_payload, timeout=5)
        if resp.status_code == 200:
            data = resp.json()
            return data.get("token")
    except Exception as e:
        print(f"[!] Auth failed: {e}")
    return None

def create_auth_header(token):
    """Return auth header."""
    if token:
        return {"Authorization": f"Bearer {token}"}
    return {}

def get_all_workflows(token):
    """Get all workflows."""
    list_url = f"{BASE_URL}/api/workflows"
    headers = create_auth_header(token)
    try:
        resp = requests.get(list_url, headers=headers, timeout=5)
        if resp.status_code == 200:
            return resp.json()
    except Exception as e:
        print(f"[!] Failed to fetch workflows: {e}")
    return []

def disable_workflow(workflow_id, token):
    """Disable a single workflow."""
    disable_url = f"{BASE_URL}/api/workflows/{workflow_id}/disable"
    headers = create_auth_header(token)
    try:
        resp = requests.post(disable_url, headers=headers, timeout=10)
        return resp.status_code in [200, 204]
    except Exception as e:
        print(f"[!] Failed to disable {workflow_id}: {e}")
    return False

def main():
    print("=" * 70)
    print("DISABLE ALL WORKFLOWS")
    print("=" * 70)
    print()

    token = get_auth_token()
    if not token:
        print("[!] ERROR: Could not obtain auth token. Aborting.")
        sys.exit(1)

    print("[*] Fetching all workflows...")
    workflows = get_all_workflows(token)
    print(f"[*] Found {len(workflows)} workflows")
    print()

    if not workflows:
        print("[*] No workflows to disable")
        return

    disabled_count = 0
    failed_count = 0

    for wf in workflows:
        wf_id = wf.get("id")
        wf_name = wf.get("name")
        is_enabled = wf.get("isEnabled", False)

        if is_enabled:
            print(f"[*] Disabling: {wf_name} ({wf_id})")
            if disable_workflow(wf_id, token):
                print(f"    [✓] Disabled")
                disabled_count += 1
            else:
                print(f"    [✗] Failed")
                failed_count += 1
        else:
            print(f"[*] Already disabled: {wf_name}")

    print()
    print("=" * 70)
    print(f"RESULTS: {disabled_count} disabled, {failed_count} failed")
    print("=" * 70)

if __name__ == "__main__":
    main()
