"""HTTP contracts shared by local administration and load-test scripts."""
import getpass
import json
import os
import urllib.error
import urllib.request


def credentials():
    user = os.environ.get("NODEPILOT_USER", "admin")
    password = os.environ.get("NODEPILOT_PASSWORD") or getpass.getpass("NodePilot password: ")
    if not password:
        raise ValueError("A NodePilot password is required")
    return user, password


def request(base_url, method, path, token=None, body=None, timeout=30, extra_headers=None):
    headers = {"Content-Type": "application/json", **(extra_headers or {})}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(f"{base_url.rstrip('/')}{path}", data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", errors="replace")


def login(http, user, password):
    status, identity = http("POST", "/api/auth/login", body={"username": user, "password": password},
                            extra_headers={"X-Auth-Token-Response": "true"})
    if status != 200 or not isinstance(identity, dict) or not identity.get("token"):
        raise RuntimeError(f"Login did not return a bearer token (HTTP {status})")
    return identity


def workflow_names(http, token):
    status, rows = http("GET", "/api/workflows/names", token=token)
    if status != 200 or not isinstance(rows, list):
        raise RuntimeError(f"Cannot list workflows (HTTP {status})")
    return rows


def find_workflow(http, token, name):
    matches = [row for row in workflow_names(http, token) if row["name"] == name]
    if len(matches) > 1:
        raise RuntimeError(f"Workflow name is ambiguous: {name}")
    if not matches:
        return None
    status, workflow = http("GET", f"/api/workflows/{matches[0]['id']}", token=token)
    if status != 200:
        raise RuntimeError(f"Cannot read workflow (HTTP {status})")
    return workflow


def publish_workflow(http, identity, name, definition):
    token = identity["token"]
    workflow = find_workflow(http, token, name)
    payload = {"name": name, "description": "Ad-hoc load-test", "definitionJson": definition}
    if workflow:
        owner = workflow.get("checkedOutByUserId")
        if owner and owner != identity.get("userId"):
            raise RuntimeError("Workflow is locked by another user")
        payload["folderId"] = workflow.get("folderId")
        if not owner:
            status, _ = http("POST", f"/api/workflows/{workflow['id']}/lock", token=token)
            if status != 200:
                raise RuntimeError(f"Cannot lock workflow (HTTP {status})")
    else:
        status, workflow = http("POST", "/api/workflows", token=token, body=payload)
        if status != 201:
            raise RuntimeError(f"Cannot create workflow (HTTP {status})")
    status, published = http("POST", f"/api/workflows/{workflow['id']}/publish", token=token, body=payload)
    if status != 200:
        raise RuntimeError(f"Cannot publish workflow (HTTP {status}); no executions started")
    return published
