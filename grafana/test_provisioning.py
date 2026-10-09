"""Offline deployment contract checks; run with Python + PyYAML, no containers."""
import json
from pathlib import Path
import unittest
import subprocess

import yaml

ROOT = Path(__file__).resolve().parent


class ProvisioningTests(unittest.TestCase):
    def test_load_stack_provisions_dashboard_and_default_datasource(self):
        stack = ROOT.parent / "tests/NodePilot.LoadTests"
        compose = yaml.safe_load((stack / "docker-compose.yml").read_text())
        mounts = dict(item.split(":")[:2] for item in compose["services"]["grafana"]["volumes"])
        self.assertEqual("/etc/grafana/provisioning", mounts.get("./grafana-provisioning"))
        for relative in ["dashboards/nodepilot.yml", "datasources/prometheus.yml"]:
            path = stack / "grafana-provisioning" / relative
            self.assertTrue(path.is_file())
            ignored = subprocess.run(["git", "check-ignore", "--no-index", str(path)], cwd=ROOT,
                                     capture_output=True, text=True)
            self.assertEqual(1, ignored.returncode, "Provisioning must ship in fresh checkouts")
        provider = yaml.safe_load((stack / "grafana-provisioning/dashboards/nodepilot.yml").read_text())["providers"][0]
        self.assertEqual("file", provider["type"])
        self.assertEqual("/var/lib/grafana/dashboards", provider["options"]["path"])
        source = yaml.safe_load((stack / "grafana-provisioning/datasources/prometheus.yml").read_text())["datasources"][0]
        self.assertEqual("prometheus", source["type"])
        self.assertEqual("http://prometheus:9090", source["url"])
        self.assertTrue(source["isDefault"])

    def test_load_dashboard_uses_exposed_metrics_and_millisecond_units(self):
        dashboard = json.loads((ROOT.parent / "tests/NodePilot.LoadTests/grafana-dashboard.json").read_text(encoding="utf-8"))
        panels = {panel["id"]: panel for panel in dashboard["panels"]}
        for panel_id, metric in [(3, "nodepilot_execution_duration_milliseconds_bucket"),
                                 (4, "nodepilot_step_duration_milliseconds_bucket")]:
            for target in panels[panel_id]["targets"]:
                self.assertIn(metric, target["expr"])
            self.assertEqual("ms", panels[panel_id]["fieldConfig"]["defaults"]["unit"])
        queue = panels[6]["targets"][0]["expr"]
        self.assertEqual("dotnet_thread_pool_queue_length_total", queue)
        self.assertIn(queue + "{", (ROOT / "RAW_METRICS_SAMPLE.txt").read_text(encoding="utf-8"))

    def test_shipped_catalogue_is_reachable_from_a_mounted_provider(self):
        compose = yaml.safe_load((ROOT / "docker-compose.yml").read_text())
        mounts = dict(item.split(":")[:2] for item in compose["services"]["grafana"]["volumes"])
        providers = [p for file in (ROOT / "grafana/provisioning/dashboards").glob("*.yml")
                     for p in yaml.safe_load(file.read_text())["providers"]]
        provider_file = ROOT / "grafana/provisioning/dashboards/dashboards.yml"
        ignored = subprocess.run(["git", "check-ignore", "--no-index", str(provider_file)],
                                 cwd=ROOT, capture_output=True, text=True)
        self.assertEqual(1, ignored.returncode, "The provider must ship in fresh checkouts, not only exist as an ignored local file")
        self.assertEqual(1, len(providers), "JSON files alone do not register a dashboard provider")
        provider = providers[0]
        self.assertEqual("file", provider["type"])
        self.assertEqual("NodePilot", provider["folder"])
        self.assertEqual("/etc/grafana/provisioning", mounts["./grafana/provisioning"])
        self.assertEqual(mounts["./grafana/dashboards"], provider["options"]["path"])
        dashboards = [json.loads(p.read_text(encoding="utf-8"))
                      for p in (ROOT / "grafana/dashboards").glob("*.json")]
        self.assertEqual(10, len(dashboards))
        self.assertEqual(10, len({d["uid"] for d in dashboards}))
        sources = yaml.safe_load((ROOT / "grafana/provisioning/datasources/prometheus.yml").read_text())
        known_uids = {d["uid"] for d in sources["datasources"]} | {"-- Grafana --", "__expr__"}

        def check_references(value):
            if isinstance(value, dict):
                source = value.get("datasource")
                if isinstance(source, dict):
                    self.assertIn(source["uid"], known_uids)
                for child in value.values():
                    check_references(child)
            elif isinstance(value, list):
                for child in value:
                    check_references(child)
        check_references(dashboards)


if __name__ == "__main__":
    unittest.main()
