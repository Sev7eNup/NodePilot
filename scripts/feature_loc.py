import os, re

ROOT = r'e:\NodePilot'
EXCLUDE = re.compile(r'[\\/](bin|obj|node_modules|dist|\.vite|TestResults|coverage-report|coverage-baseline|playwright-report|out|\.git|publish|wwwroot|logs|backup|\.build-out|\.build-obj|\.migrate-tool)[\\/]')

CSTYLE_EXTS = {'.cs', '.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs', '.css', '.sql'}
HASH_EXTS = {'.ps1', '.psm1', '.py'}

BLOCK = re.compile(r'/\*[\s\S]*?\*/')


def count_cstyle(path):
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        text = f.read()
    text = BLOCK.sub('', text)
    n = 0
    for line in text.splitlines():
        t = line.strip()
        if not t:
            continue
        if t.startswith('//'):
            continue
        n += 1
    return n


def count_hash(path):
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        lines = f.readlines()
    n = 0
    for line in lines:
        t = line.strip()
        if not t:
            continue
        if t.startswith('#'):
            continue
        n += 1
    return n


def classify(lower_path, filename_lower):
    p = lower_path

    # Tests FIRST (before any backend project pattern)
    if re.search(r'[\\/]tests[\\/]', p) or re.search(r'\.tests[\\/]', p) or 'loadtests' in p or 'testcommons' in p:
        if 'nodepilot.api.tests' in p:
            return 'Backend-Tests: API'
        if 'nodepilot.engine.tests' in p:
            return 'Backend-Tests: Engine'
        if 'nodepilot.data.tests' in p:
            return 'Backend-Tests: Data'
        if 'nodepilot.cli.tests' in p:
            return 'Backend-Tests: CLI'
        if 'loadtests' in p:
            return 'Backend-Tests: LoadTests'
        if 'testcommons' in p:
            return 'Backend-Tests: TestCommons'
        return 'Backend-Tests: Other'
    if re.search(r'nodepilot-ui[\\/]src[\\/]__tests__', p):
        return 'Frontend-Tests'

    # HA (across projects, by filename or path)
    if 'cluster' in p or 'leaderelection' in p or 'leaseepoch' in p or 'fencing' in p:
        if 'nodepilot' in p and re.search(r'src[\\/]nodepilot\.', p):
            return 'HA Active/Passive'

    # Migrations (must beat Data-Layer)
    if re.search(r'nodepilot\.data[\\/]migrations', p):
        return 'DB-Migrations'

    # ---- Frontend ----
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]properties[\\/]activities', p):
        return 'Activity-Configs (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]properties', p):
        return 'Property-Panels (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]edges', p):
        return 'Designer-Edges (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]nodes', p):
        return 'Designer-Nodes (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]debug', p):
        return 'Step-Debugger (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]timeline', p):
        return 'Execution-Timeline (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]execution', p):
        return 'Execution-Panel (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]live', p):
        return 'Live-Overlays (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]library', p):
        return 'Activity-Library (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer[\\/]overlays', p):
        return 'Designer-Overlays (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]designer', p):
        return 'Workflow-Designer Core (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]admin-settings', p):
        return 'Admin-Settings UI'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]ai', p):
        return 'AI-Features (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]dbviewer', p):
        return 'DB-Admin UI'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]workflows', p):
        return 'Workflow-Liste (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]layout', p):
        return 'Layout/Nav (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]easter-eggs', p):
        return 'Easter-Eggs'
    if re.search(r'nodepilot-ui[\\/]src[\\/]components[\\/]common', p):
        return 'Common-Components (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]pages', p):
        return 'Pages/Routes (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]hooks', p):
        return 'Hooks (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]stores', p):
        return 'State-Stores (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]api', p):
        return 'API-Client (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]i18n', p):
        return 'i18n (DE/EN)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]lib', p):
        return 'Lib/Utils (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]types', p):
        return 'Types (FE)'
    if re.search(r'nodepilot-ui[\\/]src[\\/]telemetry', p):
        return 'Telemetry (FE)'
    if 'nodepilot-ui' in p:
        return 'Frontend (sonst)'

    # ---- Backend Engine ----
    if re.search(r'nodepilot\.engine[\\/]activities', p):
        return 'Activities (Engine)'
    if re.search(r'nodepilot\.engine[\\/]triggers', p):
        return 'Triggers (Engine)'
    if re.search(r'nodepilot\.engine[\\/]execution', p):
        return 'Workflow-Engine Core'
    if re.search(r'nodepilot\.engine[\\/]debug', p):
        return 'Step-Debugger (Engine)'
    if re.search(r'nodepilot\.engine[\\/]conditions', p):
        return 'Edge-Conditions'
    if re.search(r'nodepilot\.engine[\\/]powershell', p):
        return 'PowerShell-Runtime'
    if re.search(r'nodepilot\.engine[\\/]security', p):
        return 'Security (Engine)'
    if re.search(r'nodepilot\.engine[\\/]telemetry', p):
        return 'Telemetry (Engine)'
    if re.search(r'nodepilot\.engine[\\/]options', p):
        return 'Engine-Options'
    # Engine root files → split out the big ones
    if re.search(r'nodepilot\.engine[\\/][^\\/]+\.cs$', p):
        if filename_lower in ('workflowengine.cs', 'workflowgraph.cs'):
            return 'Workflow-Engine Core'
        if filename_lower == 'steptester.cs':
            return 'Step-Test mit Kontext'
        if filename_lower == 'activityregistry.cs':
            return 'Activities (Engine)'
        if filename_lower == 'enginemetrics.cs':
            return 'Telemetry (Engine)'
        return 'Engine (root)'

    # ---- Backend API ----
    if re.search(r'nodepilot\.api[\\/]controllers', p):
        return 'API-Controllers'
    if re.search(r'nodepilot\.api[\\/]ai', p):
        return 'AI-Features (BE)'
    if re.search(r'nodepilot\.api[\\/]audit', p):
        return 'Audit-Log'
    if re.search(r'nodepilot\.api[\\/]security', p):
        return 'Auth/Security (API)'
    if re.search(r'nodepilot\.api[\\/]services', p):
        return 'API-Services'
    if re.search(r'nodepilot\.api[\\/]hubs', p):
        return 'SignalR-Hubs'
    if re.search(r'nodepilot\.api[\\/]hosting', p):
        return 'Hosting/Startup'
    if re.search(r'nodepilot\.api[\\/]configuration', p):
        return 'Config-Validators (API)'
    if re.search(r'nodepilot\.api[\\/]executiondispatch', p):
        return 'Execution-Dispatch'
    if re.search(r'nodepilot\.api[\\/]dtos', p):
        return 'API-DTOs'
    if re.search(r'nodepilot\.api[\\/]telemetry', p):
        return 'Telemetry (API)'
    if re.search(r'nodepilot\.api[\\/]logging', p):
        return 'Logging (API)'
    if re.search(r'nodepilot\.api[\\/][^\\/]+\.cs$', p):
        return 'API (root)'

    # ---- Other backend projects ----
    if 'nodepilot.data' in p:
        return 'Data-Layer (EF Core)'
    if 'nodepilot.remote' in p:
        return 'Remote/WinRM'
    if 'nodepilot.scheduler' in p:
        return 'Scheduler/Triggers (Quartz)'
    if 'nodepilot.telemetry' in p:
        return 'Telemetry-Library'
    if 'nodepilot.core' in p:
        return 'Core Domain-Models'

    # ---- CLI ----
    if re.search(r'nodepilot\.cli[\\/]commands', p):
        return 'CLI-Commands'
    if re.search(r'nodepilot\.cli[\\/]api', p):
        return 'CLI-API-Client'
    if re.search(r'nodepilot\.cli[\\/]auth', p):
        return 'CLI-Auth/TokenStore'
    if re.search(r'nodepilot\.cli[\\/]output', p):
        return 'CLI-Output'
    if re.search(r'nodepilot\.cli[\\/]settings', p):
        return 'CLI-Settings'
    if 'nodepilot.cli' in p:
        return 'CLI (root)'

    # ---- Repo-root ----
    if re.search(r'^[a-z]:[\\/]nodepilot[\\/]deploy', p):
        return 'Deploy-Skripte'
    if re.search(r'^[a-z]:[\\/]nodepilot[\\/]scripts[\\/]stress-test', p):
        return 'Stress-Tests'
    if re.search(r'^[a-z]:[\\/]nodepilot[\\/]scripts', p):
        return 'Scripts (sonst)'
    if re.search(r'^[a-z]:[\\/]nodepilot[\\/]samples', p):
        return 'Samples'
    if re.search(r'^[a-z]:[\\/]nodepilot[\\/]grafana', p):
        return 'Grafana-Dashboards'

    return 'UNKLASSIFIZIERT'


buckets = {}
for dirpath, dirnames, filenames in os.walk(ROOT):
    if EXCLUDE.search(dirpath + os.sep):
        dirnames[:] = []
        continue
    for fname in filenames:
        ext = os.path.splitext(fname)[1].lower()
        if ext not in CSTYLE_EXTS and ext not in HASH_EXTS:
            continue
        full = os.path.join(dirpath, fname)
        if EXCLUDE.search(full):
            continue
        lower = full.lower()
        label = classify(lower, fname.lower())
        try:
            n = count_hash(full) if ext in HASH_EXTS else count_cstyle(full)
        except Exception:
            continue
        if label not in buckets:
            buckets[label] = [0, 0]
        buckets[label][0] += 1
        buckets[label][1] += n

# ---- Roll up to logical feature groups ----
GROUPS = {
    'Test-Suite (Backend + FE)': [
        'Backend-Tests: API', 'Backend-Tests: Engine', 'Backend-Tests: Data',
        'Backend-Tests: CLI', 'Backend-Tests: LoadTests', 'Backend-Tests: TestCommons',
        'Backend-Tests: Other', 'Frontend-Tests',
    ],
    'Workflow-Designer (Editor-Canvas)': [
        'Workflow-Designer Core (FE)', 'Designer-Edges (FE)', 'Designer-Nodes (FE)',
        'Designer-Overlays (FE)', 'Live-Overlays (FE)', 'Activity-Library (FE)',
    ],
    'Activity-System (28 Aktivitaeten BE + UI)': [
        'Activities (Engine)', 'Activity-Configs (FE)', 'Property-Panels (FE)',
    ],
    'API-Surface (Controllers + DTOs + Hubs + Services)': [
        'API-Controllers', 'API-DTOs', 'API-Services', 'SignalR-Hubs',
        'Hosting/Startup', 'Logging (API)', 'Config-Validators (API)',
        'Execution-Dispatch', 'API (root)',
    ],
    'CLI (np)': [
        'CLI-Commands', 'CLI-API-Client', 'CLI-Auth/TokenStore',
        'CLI-Output', 'CLI-Settings', 'CLI (root)',
    ],
    'Datenbank (Layer + Migrations)': [
        'DB-Migrations', 'Data-Layer (EF Core)',
    ],
    'Frontend-Pages + Plumbing': [
        'Pages/Routes (FE)', 'Workflow-Liste (FE)', 'Hooks (FE)', 'State-Stores (FE)',
        'API-Client (FE)', 'Lib/Utils (FE)', 'Types (FE)', 'Layout/Nav (FE)',
        'Common-Components (FE)', 'Telemetry (FE)', 'Frontend (sonst)',
    ],
    'Admin-UI (Settings + DB-Admin)': [
        'Admin-Settings UI', 'DB-Admin UI',
    ],
    'Workflow-Engine Core + Conditions + PS-Runtime': [
        'Workflow-Engine Core', 'Edge-Conditions', 'PowerShell-Runtime',
        'Engine-Options', 'Engine (root)',
    ],
    'Trigger-System (Engine + Scheduler/Quartz)': [
        'Triggers (Engine)', 'Scheduler/Triggers (Quartz)',
    ],
    'Auth / Security / Audit': [
        'Auth/Security (API)', 'Security (Engine)', 'Audit-Log',
    ],
    'HA Active/Passive': ['HA Active/Passive'],
    'Step-Test & Debugger': [
        'Step-Test mit Kontext', 'Step-Debugger (FE)', 'Step-Debugger (Engine)',
        'Execution-Panel (FE)', 'Execution-Timeline (FE)',
    ],
    'AI-Features (Script + Workflow-Gen)': [
        'AI-Features (BE)', 'AI-Features (FE)',
    ],
    'Remote/WinRM': ['Remote/WinRM'],
    'Telemetry/Observability': [
        'Telemetry-Library', 'Telemetry (Engine)', 'Telemetry (API)',
    ],
    'Core Domain-Models': ['Core Domain-Models'],
    'Deploy + Scripts + Samples': [
        'Deploy-Skripte', 'Scripts (sonst)', 'Stress-Tests', 'Samples', 'Grafana-Dashboards',
    ],
    'i18n (DE/EN)': ['i18n (DE/EN)'],
    'Easter-Eggs': ['Easter-Eggs'],
}

print('=== TOP-FEATURES (logisch gruppiert) ===')
group_results = []
covered = set()
for gname, members in GROUPS.items():
    files = 0
    loc = 0
    for m in members:
        if m in buckets:
            files += buckets[m][0]
            loc += buckets[m][1]
            covered.add(m)
    group_results.append((gname, files, loc))

group_results.sort(key=lambda r: -r[2])
print(f"{'Feature':52s} {'Files':>6s} {'LoC':>10s}")
print('-' * 72)
gtotal = 0
for gname, files, loc in group_results:
    if loc == 0:
        continue
    gtotal += loc
    print(f"{gname:52s} {files:>6d} {loc:>10,d}".replace(',', '.'))
print('-' * 72)
print(f"{'GESAMT (gruppiert)':52s} {'':>6s} {gtotal:>10,d}".replace(',', '.'))

unmapped = {k: v for k, v in buckets.items() if k not in covered}
if unmapped:
    print()
    print('Nicht in Gruppen abgebildet:')
    for k, v in sorted(unmapped.items(), key=lambda kv: -kv[1][1]):
        print(f"  {k:50s} {v[0]:>6d} {v[1]:>10,d}".replace(',', '.'))
