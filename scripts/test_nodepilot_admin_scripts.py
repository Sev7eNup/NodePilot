"""Exercise administration clients against a local HTTP fixture; never run workflows."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent


def windows_powershell_environment():
    """Windows PowerShell must not inherit the module path of the PowerShell 7 that runs CI."""
    return {name: value for name, value in os.environ.items() if name.upper() != "PSMODULEPATH"}


def load_script(relative):
    spec = importlib.util.spec_from_file_location("admin_script", ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ScriptContracts(unittest.TestCase):
    def setUp(self):
        self.calls = []
        self.names = [{"id": "wf", "name": "Stress-Test"}]
        self.owner = None
        self.publish_status = 200
        self.execution_status = "Succeeded"
        self.disable_failure = None
        self.next_execution = 0
        self.alert_failure = False
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                self.respond()

            def do_POST(self):
                self.respond()

            def do_DELETE(self):
                self.respond()

            def respond(self):
                size = int(self.headers.get("Content-Length", 0))
                body = json.loads(self.rfile.read(size)) if size else None
                fixture.calls.append((self.command, self.path, body))
                code, response = 200, {}
                if self.path == "/api/auth/login":
                    response = {"userId": "caller", "username": "admin", "role": "Admin"}
                    if self.headers.get("X-Auth-Token-Response") == "true":
                        response["token"] = "fixture-token"
                elif self.headers.get("Authorization") != "Bearer fixture-token":
                    code = 401
                elif self.path == "/api/workflows/names":
                    response = fixture.names
                elif self.path == "/api/workflows/wf" and self.command == "DELETE":
                    code = 204
                elif self.path == "/api/workflows/wf":
                    response = {"id": "wf", "name": "Stress-Test", "isEnabled": True,
                                "folderId": "existing-folder", "checkedOutByUserId": fixture.owner}
                elif self.path == "/api/workflows" and self.command == "POST":
                    code, response = 201, {"id": "wf", "name": body["name"], "checkedOutByUserId": "caller"}
                elif self.path == "/api/workflows/wf/lock":
                    code, response = (409 if fixture.owner == "another-user" else 200), {"id": "wf"}
                elif self.path == "/api/workflows/wf/publish":
                    code, response = fixture.publish_status, {"id": "wf", "name": "Stress-Test"}
                elif self.path == "/api/workflows/wf/execute":
                    fixture.next_execution += 1
                    code, response = 202, {"id": f"exec{fixture.next_execution}", "status": "Pending"}
                elif self.path.startswith("/api/executions/exec"):
                    response = [] if self.path.endswith("/steps") else {
                        "id": self.path.rsplit("/", 1)[1], "status": fixture.execution_status,
                        "startedAt": "2026-01-01T00:00:00Z", "completedAt": "2026-01-01T00:00:01Z"}
                elif self.path.endswith("/disable"):
                    code = 403 if self.path == fixture.disable_failure else 204
                elif self.path.startswith("/api/alerting/"):
                    code = 403 if fixture.alert_failure else 201
                else:
                    code = 404
                encoded = json.dumps(response).encode()
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = f"http://127.0.0.1:{self.server.server_port}"
        self.environment = patch.dict(os.environ, {"NODEPILOT_PASSWORD": "fixture-only", "NODEPILOT_URL": self.url})
        self.environment.start()

    def tearDown(self):
        self.environment.stop()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def run_python(self, filename):
        module = load_script(filename)
        module.PARALLEL = 1
        module.WORKFLOW_NAME = "Stress-Test"
        with patch.object(module, "BASE_URL", self.url), patch("time.sleep"), contextlib.redirect_stdout(io.StringIO()):
            return module.main()

    def test_disable_all_includes_more_than_capped_workflow_list(self):
        self.names = [{"id": str(i), "name": f"Workflow {i}"} for i in range(501)]
        self.assertEqual(0, self.run_python("disable-all-workflows.py"))
        self.assertEqual(501, sum(path.endswith("/disable") for _, path, _ in self.calls))

    def test_disable_failure_returns_nonzero(self):
        self.disable_failure = "/api/workflows/wf/disable"
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(1, self.run_python("disable-all-workflows.py"))

    def test_python_launchers_recognize_real_success_and_poll_own_execution(self):
        for filename in ("launch-40x.py", "launch-50-master.py"):
            with self.subTest(filename=filename):
                self.assertEqual(0, self.run_python(f"stress-test/{filename}"))
        self.assertFalse(any("pageSize" in path for _, path, _ in self.calls))
        paths = [path for _, path, _ in self.calls]
        self.assertLess(paths.index("/api/workflows/wf/lock"), paths.index("/api/workflows/wf/publish"))
        self.assertLess(paths.index("/api/workflows/wf/publish"), paths.index("/api/workflows/wf/execute"))

    def test_python_launchers_report_failed_execution(self):
        self.execution_status = "Failed"
        for filename in ("launch-40x.py", "launch-50-master.py"):
            with self.subTest(filename=filename):
                self.assertEqual(1, self.run_python(f"stress-test/{filename}"))

    def test_python_launcher_reports_transport_failures_without_losing_summary(self):
        module = load_script("stress-test/launch-40x.py")
        module.PARALLEL = 1
        http = module.http_json

        def interrupted(method, path, **kwargs):
            if path.endswith("/execute"):
                raise OSError("fixture connection interrupted")
            return http(method, path, **kwargs)

        output = io.StringIO()
        with patch.object(module, "http_json", interrupted), contextlib.redirect_stdout(output):
            self.assertEqual(1, module.main())
        self.assertIn("accepted=0 failed=1", output.getvalue())

    def test_new_workflow_is_published_before_launch(self):
        self.names = []
        self.assertEqual(0, self.run_python("stress-test/launch-40x.py"))
        paths = [path for _, path, _ in self.calls]
        self.assertLess(paths.index("/api/workflows"), paths.index("/api/workflows/wf/publish"))
        self.assertNotIn("/api/workflows/wf/lock", paths)

    def test_own_lock_is_reused(self):
        self.owner = "caller"
        self.assertEqual(0, self.run_python("stress-test/launch-40x.py"))
        self.assertFalse(any(path.endswith("/lock") for _, path, _ in self.calls))

    def test_foreign_lock_and_failed_publish_never_launch(self):
        for owner, publish_status in (("another-user", 200), (None, 400)):
            self.owner, self.publish_status = owner, publish_status
            self.assertEqual(1, self.run_python("stress-test/launch-40x.py"))
        self.assertFalse(any(path.endswith("/execute") for _, path, _ in self.calls))

    def test_ambiguous_name_is_not_mutated(self):
        self.names.append({"id": "other-folder-wf", "name": "Stress-Test"})
        self.assertEqual(1, self.run_python("stress-test/launch-40x.py"))
        self.assertFalse(any(method == "POST" and path != "/api/auth/login" for method, path, _ in self.calls))

    @unittest.skipUnless(os.name == "nt", "Windows PowerShell contract")
    def test_powershell_launcher_real_http_contract_and_failure_exit(self):
        script = str(ROOT / "stress-test/launch-40x.ps1").replace("'", "''")
        for terminal, expected in (("Succeeded", 0), ("Failed", 1)):
            with self.subTest(terminal=terminal):
                self.execution_status = terminal
                # Disable host-process sampling in this HTTP contract fixture.
                command = f"function Get-NetTCPConnection {{ }}; & '{script}' -BaseUrl '{self.url}' -Password fixture-only -Parallel 1"
                result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                                        capture_output=True, text=True, encoding="utf-8", errors="replace", env=windows_powershell_environment(), timeout=120)
                self.assertEqual(expected, result.returncode, result.stdout + result.stderr)

    @unittest.skipUnless(os.name == "nt", "Windows PowerShell contract")
    def test_alert_seeds_authenticate_and_signal_partial_failure(self):
        for filename, expected_count in (("seed-custom-alert-rules.ps1", 17), ("seed-system-alert-policies.ps1", 14)):
            for fail in (False, True):
                with self.subTest(filename=filename, fail=fail):
                    self.calls.clear()
                    self.alert_failure = fail
                    script = str(ROOT / filename).replace("'", "''")
                    command = "$credential = [PSCredential]::new('admin', (ConvertTo-SecureString fixture-only -AsPlainText -Force)); "
                    command += f"& '{script}' -BaseUrl '{self.url}' -Credential $credential"
                    try:
                        result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                                                capture_output=True, text=True, encoding="utf-8", errors="replace", env=windows_powershell_environment(), timeout=60)
                    except subprocess.TimeoutExpired as timeout:
                        partial = [(part.decode("utf-8", "replace") if isinstance(part, bytes) else part or "")
                                   for part in (timeout.stdout, timeout.stderr)]
                        self.fail(f"{filename} did not finish; requests seen: {[(m, p) for m, p, _ in self.calls]}" + chr(10) + chr(10).join(partial))
                    self.assertEqual(1 if fail else 0, result.returncode, result.stdout + result.stderr)
                    requests = [body for _, path, body in self.calls if path.startswith("/api/alerting/")]
                    self.assertEqual(expected_count, len(requests))
                    self.assertTrue(all(body["isEnabled"] is False for body in requests))

    @unittest.skipUnless(os.name == "nt", "Windows PowerShell contract")
    def test_demo_seed_publishes_every_new_workflow(self):
        self.names = []
        with tempfile.TemporaryDirectory() as directory:
            demo_root = Path(directory) / "scripts" / "tech-demo"
            demo_root.mkdir(parents=True)
            script = demo_root / "seed.ps1"
            script.write_bytes((ROOT / "tech-demo/seed.ps1").read_bytes())
            for name in ("main", "child", "xl", "planar"):
                (demo_root / f"{name}.json").write_text('{"nodes":[],"edges":[]}', encoding="utf-8")
            result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-File", str(script),
                                     "-BaseUrl", self.url, "-AdminPassword", "fixture-only", "-Force"],
                                    capture_output=True, text=True, encoding="utf-8", errors="replace", env=windows_powershell_environment(), timeout=120)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        creates = [body for method, path, body in self.calls if method == "POST" and path == "/api/workflows"]
        publishes = [body for _, path, body in self.calls if path.endswith("/publish")]
        self.assertEqual(4, len(creates))
        self.assertEqual(creates, publishes)
        self.assertTrue(all(isinstance(body["definitionJson"], str) for body in creates))

    @unittest.skipUnless(os.name == "nt", "Windows PowerShell contract")
    def test_continuous_seed_finds_required_workflow_beyond_list_cap(self):
        self.names = [{"id": str(i), "name": f"Workflow {i}"} for i in range(501)]
        self.names.append({"id": "required", "name": "Required child"})
        bundle = {"schema": "nodepilot-workflow-export/v1", "workflows": [
            {"name": f"Orchestrator {i}", "description": "Fixture", "isEnabled": True,
             "definition": {"nodes": [{"data": {"activityType": "startWorkflow", "config": {"workflowNameOrId": "Required child"}}}], "edges": []}}
            for i in range(10)]}
        with tempfile.TemporaryDirectory() as directory:
            definition = Path(directory) / "bundle.json"
            definition.write_text(json.dumps(bundle), encoding="utf-8")
            result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-File",
                                     str(ROOT / "continuous-test-1min/Install-ContinuousTest1Min.ps1"),
                                     "-BaseUrl", self.url, "-Password", "fixture-only", "-DefinitionFile", str(definition)],
                                    capture_output=True, text=True, encoding="utf-8", errors="replace", env=windows_powershell_environment(), timeout=120)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(10, sum(path.endswith("/publish") for _, path, _ in self.calls))


if __name__ == "__main__":
    unittest.main()
