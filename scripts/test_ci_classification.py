"""Run the shared CI/CodeQL classifier against complete synthetic file lists."""
import os
from pathlib import Path
import shutil
import subprocess
import unittest


class ChangeClassificationTests(unittest.TestCase):
    def classify(self, paths):
        git_bash = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"
        bash = str(git_bash) if os.name == "nt" and git_bash.exists() else shutil.which("bash")
        self.assertIsNotNone(bash, "Bash is required to test the CI classifier")
        result = subprocess.run([bash, "scripts/classify-changes.sh"], input="\n".join(paths),
                                capture_output=True, text=True, timeout=15,
                                cwd=Path(__file__).resolve().parent.parent)
        self.assertEqual(0, result.returncode, result.stderr)
        return dict(line.split("=", 1) for line in result.stdout.splitlines())

    def test_large_mixed_list_keeps_all_checks(self):
        paths = ["src/Example.cs", "src/example.ts"] + [f"docs/long-directory-{i}/report.md" for i in range(10000)]
        self.assertEqual(dict(code="true", cs="true", js="true"), self.classify(paths))

    def test_documentation_only_omits_language_checks(self):
        self.assertEqual(dict(code="false", cs="false", js="false"), self.classify(["README.md", "docs/images/sample.png"]))

    def test_build_configuration_keeps_csharp_analysis(self):
        self.assertEqual(dict(code="true", cs="true", js="false"), self.classify(["Directory.Build.props"]))

    def test_empty_input_keeps_all_checks(self):
        self.assertEqual(dict(code="true", cs="true", js="true"), self.classify([]))
