#!/usr/bin/env python3
"""Disable every workflow accessible to the supplied account."""
import os
import sys

from nodepilot_http import credentials, login, request, workflow_names

BASE_URL = os.environ.get("NODEPILOT_URL", "http://localhost:5000")


def http_json(method, path, **kwargs):
    return request(BASE_URL, method, path, **kwargs)


def main():
    identity = login(http_json, *credentials())
    token = identity["token"]
    workflows = workflow_names(http_json, token)
    failed = 0
    for workflow in workflows:
        status, _ = http_json("POST", f"/api/workflows/{workflow['id']}/disable", token=token)
        if status not in (200, 204):
            failed += 1
            print(f"Failed: {workflow['name']} (HTTP {status})", file=sys.stderr)
    print(f"RESULTS: {len(workflows) - failed} disabled, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, ValueError, OSError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
