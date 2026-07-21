"""
Walks every dashboard JSON in grafana/dashboards/ and ensures every Prometheus
target on a `stat` panel has an explicit `legendFormat`. Without one, Grafana's
"value_and_name" textMode falls back to the raw `__name__{label1=...}` string,
which looks ugly and leaks query internals.

Default legendFormat = lowercased Panel title with non-alphanumeric chars
collapsed to spaces and trimmed.

Idempotent: existing legendFormat values are left alone.
"""
import json
import re
from pathlib import Path

ROOT = Path(__file__).parent / "grafana" / "dashboards"


def slug(title: str) -> str:
    s = re.sub(r"[^a-zA-Z0-9]+", " ", title).strip().lower()
    return s or "value"


def fix_panel(panel: dict) -> int:
    changed = 0
    if panel.get("type") == "stat":
        title = panel.get("title", "value")
        for tgt in panel.get("targets", []):
            if "legendFormat" not in tgt or not tgt.get("legendFormat"):
                tgt["legendFormat"] = slug(title)
                changed += 1
    # Recurse into row sub-panels (collapsed rows nest panels here).
    for sub in panel.get("panels", []) or []:
        changed += fix_panel(sub)
    return changed


def main():
    total = 0
    for path in sorted(ROOT.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        file_changed = 0
        for panel in data.get("panels", []):
            file_changed += fix_panel(panel)
        if file_changed:
            path.write_text(
                json.dumps(data, indent=2, ensure_ascii=False),
                encoding="utf-8",
            )
            print(f"  {path.name}: fixed {file_changed} target(s)")
        else:
            print(f"  {path.name}: clean")
        total += file_changed
    print(f"\nTotal targets fixed: {total}")


if __name__ == "__main__":
    main()
