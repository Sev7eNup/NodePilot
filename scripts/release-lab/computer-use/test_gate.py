import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
GATE = ROOT / 'gate.py'
sys.path.insert(0, str(ROOT))
import catalog
import gate


class GateTests(unittest.TestCase):
    def run_gate(self, *args):
        return subprocess.run([sys.executable, str(GATE), *args], cwd=ROOT.parents[2],
                              text=True, capture_output=True, encoding='utf-8')

    def test_catalog_is_complete_and_drift_checked(self):
        result = self.run_gate('catalog')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(json.loads(result.stdout)), 164)

    def test_catalog_source_hash_change_blocks(self):
        review = json.loads((ROOT / 'coverage-review.json').read_text(encoding='utf-8'))
        original = (ROOT / 'coverage-review.json').read_text(encoding='utf-8')
        try:
            review['uiSourceHash'] = '0' * 64
            (ROOT / 'coverage-review.json').write_text(json.dumps(review), encoding='utf-8')
            result = self.run_gate('catalog')
            self.assertNotEqual(result.returncode, 0)
        finally:
            (ROOT / 'coverage-review.json').write_text(original, encoding='utf-8')

    def test_status_without_run_is_rejected(self):
        result = self.run_gate('status')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('--run is required', result.stderr)

    def test_missing_preflight_blocks_case(self):
        case = catalog.cases()[0]
        state = {'preflight': {'checks': {'signatures': True}}, 'sessions': {}}
        reasons = gate.prerequisites(state, case)
        self.assertTrue(any('preflight blocked' in reason for reason in reasons))
        self.assertTrue(any('native session not attested' in reason for reason in reasons))

    def test_unsigned_dev_policy_skips_signature_requirement(self):
        config = {'release': {'version': '1.4.3-dev1'}, 'allowUnsignedDevelopmentArtifact': True}
        self.assertTrue(gate.allows_unsigned_development(config))
        self.assertNotIn('signatures', gate.required_preflight_checks(config))
        self.assertFalse(gate.allows_unsigned_development({'release': {'version': '1.4.3'}}))
        self.assertIn('signatures', gate.required_preflight_checks({'release': {'version': '1.4.3'}}))

    def test_interrupted_session_blocks_running_cases(self):
        with tempfile.TemporaryDirectory() as tmp:
            run = Path(tmp)
            (run / 'evidence').mkdir()
            case = next(c for c in catalog.cases() if c['target'] == 'desktop')
            cases = {c['id']: {'status': 'pending', 'attempts': []} for c in catalog.cases()}
            cases[case['id']] = {'status': 'running', 'attempts': [{'status': 'running', 'startedAt': gate.now()}]}
            state = {'cases': cases, 'sessions': {}}
            gate.session_record(run, state, {
                'target': 'desktop', 'status': 'interrupted', 'method': 'computer-use',
                'reason': 'interactive session was lost', 'evidence': []
            })
            self.assertEqual(state['cases'][case['id']]['status'], 'blocked')
            self.assertEqual(state['sessions']['desktop']['status'], 'interrupted')


if __name__ == '__main__':
    unittest.main()
