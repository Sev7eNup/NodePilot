"""Computer-Use release evidence ledger. Python 3.11+, standard library only."""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import uuid

# Release reports contain UI labels and evidence paths that may include
# non-ASCII characters.  Keep the CLI usable from the Windows OEM console
# and when its output is redirected to a file.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

import catalog

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STATUSES = {'pending', 'running', 'passed', 'failed', 'blocked'}


def allows_unsigned_development(config):
    """Allow unsigned artifacts only when the environment opts in explicitly."""
    version = config.get('release', {}).get('version', '')
    return bool(config.get('allowUnsignedDevelopmentArtifact')) and '-dev' in version


def required_preflight_checks(config):
    required = {'desktop', 'server', *catalog.INTEGRATIONS, 'isolation'}
    if not allows_unsigned_development(config):
        required.add('signatures')
    return required


def now():
    return datetime.now(timezone.utc).isoformat()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def object_hash(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


def write(path, value):
    path = Path(path)
    temp = path.with_suffix(path.suffix + '.tmp')
    temp.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    temp.replace(path)


def inventory(root=ROOT):
    def text(relative):
        return (root / relative).read_text(encoding='utf-8-sig')
    ui = root / 'src/nodepilot-ui/src'
    files = sorted([*ui.rglob('*.tsx'), ui / 'stores/themeStore.ts'])
    return {
        'routes': sorted(set(re.findall(r"path: '([^']+)'", text('src/nodepilot-ui/src/App.tsx')))),
        'activities': sorted(re.findall(r'^\s*(?:Action|Logic|ControlFlow|Trigger)\("([^"]+)"',
            text('src/NodePilot.Core/Activities/ActivityCatalog.cs'), re.M)),
        'settings': sorted(re.findall(r'new SettingsSectionDescriptor\(\s*(?://[^\n]*\n\s*)*(?:SectionPath:\s*)?"([^"]+)"',
            text('src/NodePilot.Api/Configuration/SettingsSchema.cs'))),
        'themes': sorted(re.findall(r"\{ id: '([^']+)'", text('src/nodepilot-ui/src/stores/themeStore.ts')) + ['system']),
        'uiSourceHash': object_hash({str(p.relative_to(root)).replace('\\', '/'): hashlib.sha256(
            p.read_text(encoding='utf-8-sig').replace('\r\n', '\n').encode()).hexdigest() for p in files}),
    }


def validate_catalog(root=ROOT, review_path=HERE / 'coverage-review.json'):
    cases = catalog.cases()
    require(bool(cases), 'Empty catalog')
    ids = [c['id'] for c in cases]
    require(len(ids) == len(set(ids)), 'Duplicate case ID')
    for c in cases:
        require(re.fullmatch(r'[A-Za-z0-9-]+', c['id']), 'Unsafe case ID')
        require(c['target'] in ('desktop', 'server'), 'Unknown target')
        for key in ('steps', 'expected', 'evidence', 'prerequisites'):
            require(c[key] and all(isinstance(x, str) and x.strip() for x in c[key]), f"{c['id']}: empty {key}")
        require(c['cleanup'].strip(), 'Missing cleanup')
    actual = inventory(root)
    for key, expected in [('routes', catalog.ROUTES), ('activities', catalog.ACTIVITIES),
                          ('settings', catalog.SETTINGS), ('themes', catalog.THEMES)]:
        require(actual[key] == sorted(expected), f'Catalog drift in {key}: source={actual[key]}, catalog={sorted(expected)}')
    review = read(review_path)
    require(review['uiSourceHash'] == actual['uiSourceHash'],
            'UI sources changed: review new/changed controls and dialogs, update cases, then refresh coverage-review.json')
    require(bool(review.get('reviewNote')), 'Coverage review needs a rationale')
    covers = {item for c in cases for item in c['covers']}
    for kind, values in [('route', catalog.ROUTES), ('activity', catalog.ACTIVITIES),
                         ('setting', catalog.SETTINGS), ('theme', catalog.THEMES), ('integration', catalog.INTEGRATIONS)]:
        require(all(kind + ':' + v in covers for v in values), 'Unmapped ' + kind)
    return cases


def resolve_from(config_path, path):
    candidate = Path(path)
    return candidate.resolve() if candidate.is_absolute() else (Path(config_path).resolve().parent / candidate).resolve()


def binding(config_path):
    config_path = Path(config_path).resolve()
    config = read(config_path)
    require(config.get('schemaVersion') == 1, 'Unsupported environment schema')
    version = config['release']['version']
    require(re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?', version), 'Invalid release version')
    commit = config['release']['commit']
    require(re.fullmatch(r'[a-f0-9]{40}', commit), 'Release commit must be a full SHA')
    require(config['environmentRevision'].strip(), 'Missing environment revision')
    files = {}
    config_allows_unsigned = allows_unsigned_development(config)
    artifact_keys = ('desktopInstaller', 'serverInstaller', 'checksums')
    if not config_allows_unsigned or Path(resolve_from(config_path, config['release'].get('publisherCertificate', ''))).is_file():
        artifact_keys += ('publisherCertificate',)
    for key in artifact_keys:
        path = resolve_from(config_path, config['release'][key])
        require(path.is_file(), 'Missing release artifact: ' + str(path))
        files[key] = {'path': str(path), 'sha256': digest(path), 'size': path.stat().st_size,
                      'mtimeNs': path.stat().st_mtime_ns}
    sums = Path(files['checksums']['path']).read_text(encoding='utf-8-sig')
    entries = {name.strip().lstrip('*'): sha.lower() for sha, name in re.findall(r'^([a-fA-F0-9]{64})\s+(.+)$', sums, re.M)}
    for key in ('desktopInstaller', 'serverInstaller'):
        item = files[key]
        require(entries.get(Path(item['path']).name) == item['sha256'], 'Checksum mismatch: ' + key)
    if 'publisherCertificate' in files:
        item = files['publisherCertificate']
        require(entries.get(Path(item['path']).name) == item['sha256'], 'Checksum mismatch: publisherCertificate')
    for target in ('desktop', 'server'):
        expected = f"NodePilot-{target.title()}-Setup-{version}.exe"
        require(Path(files[target + 'Installer']['path']).name == expected, 'Installer version/name mismatch')
        entry = config['targets'][target]
        require(entry['disposable'] is True and entry['vm'] and entry['checkpoint'], 'Target must be an explicit disposable VM/checkpoint')
    require(config['targets']['desktop']['vm'] != config['targets']['server']['vm'], 'Targets must be separate VMs')
    return {'release': {'version': version, 'commit': commit}, 'artifacts': files,
            'environmentHash': object_hash(config), 'catalogHash': object_hash(catalog.cases()),
            'coverageHash': digest(HERE / 'coverage-review.json'), 'runnerHash': digest(Path(__file__))}


@contextmanager
def locked(run):
    run = Path(run).resolve()
    lock = run / '.writer.lock'
    with lock.open('x', encoding='utf-8') as stream:
        stream.write(now())
    try:
        yield run
    finally:
        lock.unlink()


def load_run(run, check_binding=True):
    run = Path(run).resolve()
    state = read(run / 'run.json')
    require(state['schemaVersion'] == 1, 'Unsupported run schema')
    validate_catalog()
    cases = catalog.cases()
    require(set(state['cases']) == {c['id'] for c in cases}, 'Incomplete or foreign case list')
    require(state['binding']['catalogHash'] == object_hash(cases), 'Catalog changed: start a new run')
    if check_binding:
        require(state['binding'] == binding(state['configPath']), 'Artifacts, environment or runner changed: start a new run')
    else:
        require(state['binding']['environmentHash'] == object_hash(read(state['configPath'])), 'Environment changed')
        require(state['binding']['runnerHash'] == digest(Path(__file__)), 'Runner changed')
        require(state['binding']['coverageHash'] == digest(HERE / 'coverage-review.json'), 'Coverage review changed')
        for artifact in state['binding']['artifacts'].values():
            stat = Path(artifact['path']).stat()
            require((stat.st_size, stat.st_mtime_ns) == (artifact['size'], artifact['mtimeNs']), 'Artifact changed')
    return state


def initialize(config_path, run):
    validate_catalog()
    bound = binding(config_path)
    run = Path(run).resolve()
    require(not run.exists(), 'Run directory exists; use status/next to resume')
    run.mkdir(parents=True)
    (run / 'evidence').mkdir()
    state = dict(schemaVersion=1, id=str(uuid.uuid4()), createdAt=now(), configPath=str(Path(config_path).resolve()),
        binding=bound, preflight=None, sessions={}, cleanup=None, findings=[], exceptions=[],
        cases={c['id']: dict(status='pending', attempts=[]) for c in catalog.cases()})
    write(run / 'run.json', state)
    write(run / 'catalog.json', catalog.cases())
    return state


def evidence(run, paths, screenshot=False):
    run = Path(run).resolve()
    result = []
    for value in paths:
        p = (run / value).resolve()
        require(p.is_relative_to(run / 'evidence') and p.is_file(), 'Evidence must be a file under run/evidence')
        require(p.stat().st_size > 0, 'Empty evidence file')
        result.append({'path': str(p.relative_to(run)).replace('\\', '/'), 'sha256': digest(p)})
    if screenshot:
        require(any((run / item['path']).read_bytes()[:8] == b'\x89PNG\r\n\x1a\n' for item in result), 'A PNG screenshot is required')
    return result


def check_evidence(run, items, screenshot=False):
    require(evidence(run, [i['path'] for i in items], screenshot) == items, 'Evidence was changed after recording')


def native_ready(state, target):
    session = state['sessions'].get(target)
    return session and session['status'] == 'ready'


def prerequisites(state, case):
    result = []
    preflight = state['preflight']
    checks = preflight['checks'] if preflight else {}
    config = read(state['configPath']) if state.get('configPath') else {'release': {'version': '0.0.0'}}
    requirements = ['isolation', case['target'], *[p for p in case['prerequisites'] if p in catalog.INTEGRATIONS]]
    if not allows_unsigned_development(config):
        requirements.insert(0, 'signatures')
    for requirement in requirements:
        if checks.get(requirement) is not True:
            result.append('preflight blocked: ' + requirement)
    if not native_ready(state, case['target']):
        result.append('native session not attested for ' + case['target'])
    return result


def session_record(run, state, data):
    target = data['target']
    require(target in ('desktop', 'server'), 'Invalid target')
    require(data['status'] in ('ready', 'interrupted'), 'Invalid session status')
    require(data.get('method') == 'computer-use', 'Native Computer Use is required')
    if data['status'] == 'ready':
        for key in ('window', 'identity', 'screenshotObserved', 'mouseObserved', 'keyboardObserved'):
            require(data.get(key), 'Missing session proof: ' + key)
        data['evidence'] = evidence(run, data['evidence'], screenshot=True)
    else:
        require(data.get('reason'), 'Interruption needs a reason')
        data['evidence'] = evidence(run, data.get('evidence', []))
        for case in catalog.cases():
            entry = state['cases'][case['id']]
            if case['target'] == target and entry['status'] == 'running':
                entry['status'] = 'blocked'
                entry['attempts'][-1].update(status='blocked', endedAt=now(), observation=data['reason'])
    data['recordedAt'] = now()
    state['sessions'][target] = data


def start_case(state, id, retry=False):
    require(state['cleanup'] is None, 'Run is already cleaned up')
    entry = state['cases'][id]
    case = next(c for c in catalog.cases() if c['id'] == id)
    require(not prerequisites(state, case), '; '.join(prerequisites(state, case)))
    require(not any(e['status'] == 'running' for e in state['cases'].values()), 'Only one GUI case may run at a time')
    require(entry['status'] == 'pending' or (retry and entry['status'] in ('failed', 'blocked')), 'Explicit retry required; passed cases are immutable')
    entry['status'] = 'running'
    entry['attempts'].append(dict(status='running', startedAt=now(), session=state['sessions'][case['target']]))


def record_case(run, state, id, data):
    entry = state['cases'][id]
    case = next(c for c in catalog.cases() if c['id'] == id)
    require(entry['status'] == 'running', 'Start the case before recording its result')
    require(data['status'] in ('passed', 'failed', 'blocked'), 'Invalid terminal result')
    require(data.get('observation', '').strip(), 'Missing observation')
    require(data.get('method') == 'computer-use', 'API/DOM/Playwright results cannot pass a Computer-Use case')
    if data['status'] == 'passed':
        require(native_ready(state, case['target']), 'Session interrupted')
        require(data.get('checks') == [True] * len(case['expected']), 'Every expected checkpoint must be explicitly checked')
    data['evidence'] = evidence(run, data.get('evidence', []), screenshot=data['status'] == 'passed')
    timing = data.get('timing', {})
    require(set(timing) == {'navigationSeconds', 'waitingSeconds', 'preparationSeconds'}, 'Provide the three timing counters')
    require(all(type(v) in (int, float) and v >= 0 for v in timing.values()), 'Invalid timing')
    attempt = entry['attempts'][-1]
    duration = (datetime.now(timezone.utc) - datetime.fromisoformat(attempt['startedAt'])).total_seconds()
    require(sum(timing.values()) <= duration + 1, 'Timing components exceed elapsed time')
    attempt.update(data, endedAt=now(), durationSeconds=duration)
    entry['status'] = data['status']
    if data['status'] == 'failed':
        state['findings'].append(dict(id='F-' + str(len(state['findings']) + 1), case=id,
            observation=data['observation'], openedAt=now(), resolution=None))


def verdict(run, state):
    problems = []
    exceptions = {e['subject'] for e in state['exceptions']}
    for e in state['exceptions']:
        check_evidence(run, e['evidence'])
    if not state['preflight'] or state['preflight']['status'] != 'passed':
        problems.append('Environment preflight has not passed')
    else:
        check_evidence(run, state['preflight']['evidence'])
    for c in catalog.cases():
        entry = state['cases'][c['id']]
        require(entry['status'] in STATUSES, 'Unknown case status')
        if entry['status'] == 'passed':
            require(bool(entry['attempts']), 'Passed case without an attempt')
            attempt = entry['attempts'][-1]
            require(attempt['status'] == 'passed' and attempt['method'] == 'computer-use', 'Invalid passed attempt')
            require(attempt['checks'] == [True] * len(c['expected']), 'Incomplete assertions')
            require(attempt['endedAt'] >= attempt['startedAt'], 'Invalid timestamps')
            require(attempt['session']['status'] == 'ready', 'Missing session provenance')
            check_evidence(run, attempt['session']['evidence'], screenshot=True)
            check_evidence(run, attempt['evidence'], screenshot=True)
        elif c['id'] not in exceptions or entry['status'] == 'running':
            problems.append(c['id'] + ': ' + entry['status'])
    for finding in state['findings']:
        if not finding['resolution'] and finding['id'] not in exceptions:
            problems.append(finding['id'] + ': unresolved finding in ' + finding['case'])
    if not state['cleanup'] or state['cleanup']['status'] != 'passed':
        problems.append('Cleanup has not passed')
    else:
        check_evidence(run, state['cleanup']['evidence'])
    return problems


def report(run, state, problems):
    counts = Counter(v['status'] for v in state['cases'].values())
    rows = ['# Computer-Use release acceptance', '',
        f"Release: {state['binding']['release']['version']} / {state['binding']['release']['commit']}",
        f"Run: {state['id']}", f"Verdict: {'BLOCKED' if problems else 'PASS WITH EXPLICIT EXCEPTIONS' if state['exceptions'] else 'PASS'}",
        '', str(dict(counts)), '', '| Case | Target | Status | Seconds |', '|---|---|---|---|']
    slow = []
    for c in catalog.cases():
        entry = state['cases'][c['id']]
        seconds = sum(a.get('durationSeconds', 0) for a in entry['attempts'])
        slow.append((seconds, c['id']))
        rows.append(f"| {c['id']} | {c['target']} | {entry['status']} | {seconds:.1f} |")
    rows += ['', '## Slowest cases', *[f'- {id}: {seconds:.1f}s' for seconds, id in sorted(slow, reverse=True)[:10]],
             '', '## Blocking results', *['- ' + p for p in problems], '', '## Findings']
    rows += ['- ' + f['id'] + ': ' + f['observation'].replace('\n', ' ') for f in state['findings']]
    rows += ['', '## Explicit exceptions', *['- ' + e['subject'] + ': ' + e['reason'] for e in state['exceptions']]]
    (Path(run) / 'summary.md').write_text('\n'.join(rows) + '\n', encoding='utf-8')


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('command', choices=['catalog', 'init', 'preflight', 'session', 'next', 'start', 'record',
                                      'status', 'verify', 'cleanup', 'exception', 'resolve'])
    p.add_argument('--config', type=Path)
    p.add_argument('--run', type=Path)
    p.add_argument('--input', type=Path, help='JSON receipt/proof file; never put secrets on the command line')
    p.add_argument('--case')
    p.add_argument('--retry', action='store_true')
    p.add_argument('--pilot', action='store_true', help='Filter next only; never relaxes the release gate')
    a = p.parse_args(argv)
    try:
        if a.command == 'catalog':
            print(json.dumps(validate_catalog(), indent=2, ensure_ascii=False)); return 0
        require(a.run is not None, '--run is required')
        if a.command == 'init':
            require(a.config is not None, '--config is required')
            initialize(a.config, a.run); print('Created pending run:', a.run); return 0
        with locked(a.run):
            # Full artifact hashing at init, resume/status and final verification; cheap file
            # identity checks between cases avoid rereading gigabytes for every observation.
            state = load_run(a.run, check_binding=a.command in ('status', 'verify', 'preflight'))
            if a.command in ('status', 'verify'):
                problems = verdict(a.run, state)
                report(a.run, state, problems)
                print(json.dumps({'counts': dict(Counter(e['status'] for e in state['cases'].values())), 'blocking': problems}, indent=2))
                return 1 if problems and a.command == 'verify' else 0
            if a.command == 'next':
                candidates = [c for c in catalog.cases() if state['cases'][c['id']]['status'] == 'pending'
                    and (not a.pilot or c['id'] in catalog.PILOT)]
                for c in candidates:
                    if not prerequisites(state, c):
                        print(json.dumps(c, indent=2, ensure_ascii=False)); return 0
                print('No runnable pending case. Inspect status, preflight and sessions; failed/blocked cases require explicit retry.')
                return 1
            if a.command == 'start':
                require(a.case in state['cases'], 'Unknown case ID')
                start_case(state, a.case, a.retry)
            else:
                require(a.input is not None, '--input is required')
                data = read(a.input)
                if a.command == 'record':
                    require(a.case in state['cases'], 'Unknown case ID')
                    record_case(a.run, state, a.case, data)
                elif a.command == 'session':
                    session_record(a.run, state, data)
                elif a.command == 'preflight':
                    require(not any(e['attempts'] for e in state['cases'].values()), 'Preflight is fixed once case execution starts')
                    require(data['binding'] == state['binding'], 'Preflight belongs to another release/environment')
                    config = read(state['configPath'])
                    required = required_preflight_checks(config)
                    require(set(data['checks']) == required or
                            (allows_unsigned_development(config) and set(data['checks']) == required | {'signatures'}),
                            'Preflight checks incomplete')
                    require(all(type(v) is bool for v in data['checks'].values()), 'Preflight checks must be booleans')
                    signature_ok = data['checks'].get('signatures', True) or allows_unsigned_development(config)
                    data['status'] = 'passed' if all(data['checks'].get(k, False) for k in required) and signature_ok else 'blocked'
                    data['evidence'] = evidence(a.run, data['evidence'])
                    require(data['evidence'], 'Preflight needs inspection evidence')
                    state['preflight'] = data
                elif a.command == 'cleanup':
                    require(not any(e['status'] == 'running' for e in state['cases'].values()), 'Interrupt or finish the active case before cleanup')
                    require(data['status'] in ('passed', 'blocked'), 'Invalid cleanup status')
                    require(data.get('restoredTargets') == ['desktop', 'server'], 'Both target baselines must be restored')
                    require(data.get('externalFixturesRemoved') is True and data.get('temporaryAccessRemoved') is True, 'Cleanup incomplete')
                    data['evidence'] = evidence(a.run, data['evidence'])
                    require(data['evidence'], 'Cleanup needs evidence')
                    state['cleanup'] = data
                elif a.command == 'exception':
                    subjects = set(state['cases']) | {f['id'] for f in state['findings']}
                    require(data['subject'] in subjects, 'Only case/finding exceptions allowed; infrastructure cannot be waived')
                    require(data.get('approvedBy') == 'user' and data.get('reason') and data.get('approvalReference'), 'An explicit user decision is required')
                    data['evidence'] = evidence(a.run, data['evidence'])
                    require(data['evidence'], 'Attach the actual user decision')
                    state['exceptions'].append(data)
                elif a.command == 'resolve':
                    finding = next(f for f in state['findings'] if f['id'] == data['finding'])
                    require(data.get('classification') == 'harness' and data.get('reason'), 'Product fixes need a new artifact/run; only diagnosed harness errors can be resolved here')
                    require(state['cases'][finding['case']]['status'] == 'passed', 'A successful explicit retry is required')
                    data['evidence'] = evidence(a.run, data['evidence'])
                    require(data['evidence'], 'Attach diagnosis evidence')
                    finding['resolution'] = data
            write(a.run / 'run.json', state)
        return 0
    except (ValueError, KeyError, OSError, StopIteration, TypeError, json.JSONDecodeError) as exc:
        print('BLOCKED:', str(exc), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
