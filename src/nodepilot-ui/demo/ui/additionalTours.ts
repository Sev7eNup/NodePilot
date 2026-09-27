import type { createBrowserRouter } from 'react-router';
import { FILE_WORKFLOW_ID } from '../run/fileScenario';
import { definitionOf, getWorld, subscribeWorld } from '../state/world';
import { ensureMissionWorkflow, MISSION_WORKFLOW_IDS, type NewMissionId } from '../seed/missionFixtures';
import { demoId } from '../state/ids';
import { missionPlan } from '../run/missionScenarios';
import { demoLanguage } from './strings';

const IDS = ['build', 'decision', 'parallel', 'service', 'live', 'versions', 'machine', 'maintenance'] as const;
export function isNewMissionId(value: string | null): value is NewMissionId {
  return IDS.some(id => id === value);
}

type Stage = 'work' | 'publish' | 'run' | 'running' | 'answer' | 'retry' | 'done' | 'reset';
type MissionCopy = { title: string; work: string; publish?: string; run?: string; running?: string; answer?: string; retry?: string; done: string };
const COPY: Record<'de' | 'en', Record<NewMissionId, MissionCopy>> = {
  de: {
    build: { title: 'Bauen eines Workflows', work: 'Verbinde im Designer Manual Trigger → PowerShell → Return Data und speichere den Entwurf. Die drei Activities sind bereits konfiguriert.', publish: 'Beide Verbindungen sind gespeichert. Veröffentliche den Workflow über die obere Werkzeugleiste.', run: 'Starte den veröffentlichten Workflow über Test-Run.', running: 'Verfolge deinen Lauf. Der Abschluss wird anhand der Ausführung geprüft.', done: 'Dein eigener Workflow ist verbunden, veröffentlicht und erfolgreich gelaufen.' },
    decision: { title: 'Verfolge den Weg', work: 'Starte diesen Workflow mit freeSpaceGb = 8. Verfolge danach nur den kritischen Pfad.', running: 'Die Entscheidung wird ausgeführt. Öffne danach das Ergebnis und prüfe den kritischen Zweig.', retry: 'Starte erneut mit freeSpaceGb = 8 und prüfe die ausgeführten Activities.', done: 'Bei 8 GB lief nur der kritische Pfad mit Ordneranalyse und Warnmail.' },
    parallel: { title: 'Parallele Arbeit beobachten', work: 'Starte die Bereinigung mit den vorgeschlagenen Werten. Prüfsumme und Löschen starten nach der Archivierung gemeinsam.', running: 'Beobachte beide Zweige. Die Zusammenführung wartet auf beide Ergebnisse.', retry: 'Dieser Lauf zeigt nicht beide abgeschlossenen Zweige. Starte ihn erneut.', done: 'Prüfsumme und Bereinigung liefen überlappend. Erst danach folgten waitAll und Abschlussmail.' },
    service: { title: 'Einen Dienst wiederherstellen', work: 'Starte den Workflow mit serviceState = Stopped. Verfolge Start, Wartezeit und erneute Statusprüfung.', running: 'Der Dienst wird simuliert gestartet und danach erneut gelesen.', retry: 'Starte erneut mit serviceState = Stopped.', done: 'Der gestoppte Dienst wurde gestartet, erneut geprüft und als wiederhergestellt gemeldet.' },
    live: { title: 'Greife live in einen Lauf ein', work: 'Starte den Live Ops Check. Der lange Prüfschritt gibt dir Zeit für den Abbruch.', running: 'Wechsle in der Navigation zu Live Ops, öffne den laufenden Live Ops Check und klicke dort auf Abbrechen.', retry: 'Der Lauf endete ohne Abbruch. Starte ihn erneut und brich ihn in Live Ops ab.', done: 'Du hast eine laufende Ausführung über Live Ops abgebrochen.' },
    versions: { title: 'Zwei Workflow Versionen vergleichen', work: 'Öffne im Designer den Versionsvergleich und wähle die ältere Version. Vergleiche sie mit dem aktuellen Workflow.', answer: 'Welche Activity fehlt in der älteren Version?', done: 'Die ältere Version enthält noch keine Return Data Activity.' },
    machine: { title: 'Prüfung einer Zielmaschine', work: 'Suche in der Maschinenliste nach LAB01. Lies Erreichbarkeit und Anzahl verwendender Workflows.', answer: 'Was zeigen die Daten für LAB01?', done: 'LAB01 ist nicht erreichbar. Da kein Workflow sie verwendet, ist aktuell keiner direkt betroffen.' },
    maintenance: { title: 'Plane ein Wartungsfenster', work: 'Bearbeite Patch night: Samstag von 23:00 bis 02:00 Uhr, weiterhin als Blackout für den Ordner Operations. Speichere die Änderung.', done: 'Patch night ist für Samstag 23:00–02:00 Uhr und den Ordner Operations gespeichert.' },
  },
  en: {
    build: { title: 'Build a workflow', work: 'In the designer, connect Manual Trigger → PowerShell → Return Data and save the draft. The three activities are configured for you.', publish: 'Both connections are saved. Publish the workflow from the toolbar.', run: 'Run the published workflow with Test Run.', running: 'Follow your execution. Completion is checked against its result.', done: 'You connected, published and successfully ran your own workflow.' },
    decision: { title: 'Follow a decision', work: 'Run this workflow with freeSpaceGb = 8. Follow the critical path.', running: 'The decision is running. Open the result and inspect the critical branch.', retry: 'Run again with freeSpaceGb = 8 and inspect the activities.', done: 'At 8 GB, only the critical path ran, including folder analysis and the alert email.' },
    parallel: { title: 'Watch parallel work', work: 'Start cleanup with the suggested values. Checksum and removal begin together after archiving.', running: 'Watch both branches. The junction waits for both results.', retry: 'This run did not show both completed branches. Start it again.', done: 'Checksum and removal overlapped. The waitAll junction and summary email came afterwards.' },
    service: { title: 'Recover a service', work: 'Run with serviceState = Stopped. Follow the start, delay and second status check.', running: 'The service starts in the simulation and is then checked again.', retry: 'Run again with serviceState = Stopped.', done: 'The stopped service was started, checked again and reported as recovered.' },
    live: { title: 'Control a live run', work: 'Start Live Ops Check. Its long check gives you time to cancel.', running: 'Use the navigation to open Live Ops. Select the running Live Ops Check and click Cancel there.', retry: 'The run finished without cancellation. Start it again and cancel in Live Ops.', done: 'You cancelled a running execution in Live Ops.' },
    versions: { title: 'Compare two versions', work: 'Open version comparison in the designer and select the older version. Compare it with the current workflow.', answer: 'Which activity is missing from the older version?', done: 'The older version does not yet contain Return Data.' },
    machine: { title: 'Inspect a target machine', work: 'Search the machine list for LAB01. Read its reachability and workflow count.', answer: 'What do the LAB01 details show?', done: 'LAB01 is unreachable. No workflow currently uses it.' },
    maintenance: { title: 'Plan a maintenance window', work: 'Edit Patch night: Saturday 23:00–02:00, still a Blackout for the Operations folder. Save the change.', done: 'Patch night now covers Saturday 23:00–02:00 and the Operations folder.' },
  },
};

const UI = {
  de: { badge: 'GEFÜHRTE AUFGABE', close: 'Führung beenden', resume: 'Zur Aufgabe zurück', result: 'Ausführung öffnen', explore: 'Demo frei erkunden', website: 'Zur NodePilot-Website', reset: 'Demo zurücksetzen', resetText: 'Die Beispieldaten wurden in diesem Tab verändert. Setze die Demo zurück, um diese Aufgabe mit ihrem Ausgangszustand zu starten.', wrong: 'Sieh dir die Angaben in der Produktoberfläche noch einmal an.', notice: 'Simulierte Daten · Änderungen gelten nur in diesem Tab', answers: { versions: [['return', 'Return Data'], ['script', 'PowerShell'], ['copy', 'File Copy']], machine: [['none', 'Nicht erreichbar · 0 Workflows'], ['used', 'Nicht erreichbar · mehrere Workflows'], ['online', 'Erreichbar · 0 Workflows']] } },
  en: { badge: 'GUIDED TASK', close: 'End walkthrough', resume: 'Return to the task', result: 'Open execution', explore: 'Explore the demo', website: 'Back to NodePilot', reset: 'Reset demo', resetText: 'The example data has changed in this tab. Reset the demo to start this task from its original state.', wrong: 'Check the details in the product interface again.', notice: 'Simulated data · changes stay in this tab', answers: { versions: [['return', 'Return Data'], ['script', 'PowerShell'], ['copy', 'File Copy']], machine: [['none', 'Unreachable · 0 workflows'], ['used', 'Unreachable · several workflows'], ['online', 'Reachable · 0 workflows']] } },
} as const;

function taskRoute(mode: NewMissionId): string {
  if (mode in MISSION_WORKFLOW_IDS) return `/workflows/${MISSION_WORKFLOW_IDS[mode as keyof typeof MISSION_WORKFLOW_IDS]}`;
  if (mode === 'versions') return `/workflows/${FILE_WORKFLOW_ID}`;
  if (mode === 'machine') return '/machines';
  return '/maintenance-windows';
}

function findTaskRun(mode: NewMissionId, baseline: Set<string>) {
  const workflowId = MISSION_WORKFLOW_IDS[mode as keyof typeof MISSION_WORKFLOW_IDS];
  return getWorld().executions.find(run => run.workflowId === workflowId && !baseline.has(run.id));
}

function runMatches(mode: NewMissionId, runId: string): boolean {
  const world = getWorld();
  const run = world.executions.find(entry => entry.id === runId);
  if (!run) return false;
  const steps = world.steps.get(runId) ?? [];
  const succeeded = (id: string) => steps.some(step => step.stepId === id && step.status === 'Succeeded');
  if (mode === 'build') {
    const workflow = world.workflows.find(entry => entry.id === MISSION_WORKFLOW_IDS.build);
    const edges = workflow ? definitionOf(workflow).edges : [];
    return run.status === 'Succeeded' && workflow?.isEnabled === true && edges.some(edge => edge.source === 'trigger' && edge.target === 'script') && edges.some(edge => edge.source === 'script' && edge.target === 'result') && succeeded('result');
  }
  if (mode === 'decision') {
    const inputs = JSON.parse(run.inputParametersJson ?? '{}') as Record<string, string>;
    return run.status === 'Succeeded' && Number(inputs.freeSpaceGb) === 8 && succeeded('top_dirs') && succeeded('mail_critical') && !steps.some(step => ['log_warning', 'log_ok'].includes(step.stepId));
  }
  if (mode === 'service') {
    const inputs = JSON.parse(run.inputParametersJson ?? '{}') as Record<string, string>;
    const ordered = ['start', 'settle', 'verify', 'mail_recovered'].map(id => steps.find(step => step.stepId === id));
    return run.status === 'Succeeded' && inputs.serviceState?.toLowerCase() === 'stopped' && ['start', 'settle', 'verify', 'mail_recovered', 'gather'].every(succeeded) && !steps.some(step => step.stepId === 'mail_escalate')
      && ordered.every((step, index) => index === 0 || !!step?.startedAt && !!ordered[index - 1]?.completedAt && Date.parse(step.startedAt) >= Date.parse(ordered[index - 1]!.completedAt!));
  }
  if (mode === 'parallel') {
    const checksum = steps.find(step => step.stepId === 'checksum');
    const purge = steps.find(step => step.stepId === 'purge');
    const gather = steps.find(step => step.stepId === 'gather');
    const mail = steps.find(step => step.stepId === 'mail');
    return run.status === 'Succeeded' && !!checksum?.startedAt && !!checksum.completedAt && !!purge?.startedAt && !!purge.completedAt && !!gather?.startedAt && !!gather.completedAt && !!mail?.startedAt
      && Date.parse(checksum.startedAt) < Date.parse(purge.completedAt) && Date.parse(purge.startedAt) < Date.parse(checksum.completedAt)
      && Date.parse(gather.startedAt) >= Math.max(Date.parse(checksum.completedAt), Date.parse(purge.completedAt))
      && Date.parse(mail.startedAt) >= Date.parse(gather.completedAt ?? '');
  }
  return mode === 'live' && run.status === 'Cancelled';
}

/** The eight additional tasks use one panel and one state machine; the original two retain their URLs. */
export function mountAdditionalTours(router: ReturnType<typeof createBrowserRouter>) {
  const panel = document.createElement('aside');
  panel.className = 'np-tour';
  panel.hidden = true;
  let active = false;
  let mode: NewMissionId = 'build';
  let stage: Stage = 'work';
  let baseline = new Set<string>();
  let seenVersion = false;
  let seenMachine = false;
  let wrong = false;
  let initialWindowTime = '';
  const requestedLanguage = new URLSearchParams(location.search).get('lang');

  function navigate(path: string): void {
    const next = new URL(path, location.origin);
    next.searchParams.set('tour', mode);
    if (requestedLanguage === 'en' || requestedLanguage === 'de') next.searchParams.set('lang', requestedLanguage);
    void router.navigate(next.pathname + next.search);
  }

  function end(): void {
    active = false;
    panel.hidden = true;
    panel.remove();
    document.body.classList.remove('np-tour-open');
    const next = new URL(location.href);
    next.searchParams.delete('tour');
    void router.navigate(next.pathname.slice('/demo'.length) + next.search, { replace: true });
  }

  function button(label: string, action: string, handler: () => void, quiet = false): HTMLButtonElement {
    const element = document.createElement('button');
    element.type = 'button';
    element.textContent = label;
    element.dataset.tourAction = action;
    if (quiet) element.className = 'np-tour-quiet';
    element.addEventListener('click', handler);
    return element;
  }

  function text<K extends keyof HTMLElementTagNameMap>(tag: K, value: string): HTMLElementTagNameMap[K] {
    const element = document.createElement(tag);
    element.textContent = value;
    return element;
  }

  function update(): void {
    if (!active || stage === 'done' || stage === 'reset') return;
    const world = getWorld();
    if (mode === 'maintenance') {
      const patch = world.maintenanceWindows.find(window => window.id === demoId('maintenance-window:patch-night'));
      const operations = demoId('folder:operations');
      if (patch && initialWindowTime !== '1380:120' && patch.weeklyStartMinuteOfDay === 1380 && patch.weeklyEndMinuteOfDay === 120 && patch.weeklyDaysMask === 64 && patch.mode === 'Blackout' && patch.scopeKind === 'Folders' && patch.targets.some(target => target.targetKind === 'Folder' && target.targetId === operations)) stage = 'done';
    } else if (mode === 'versions') {
      if (seenVersion) stage = 'answer';
    } else if (mode === 'machine') {
      if (seenMachine) stage = 'answer';
    } else {
      const run = findTaskRun(mode, baseline);
      if (run && runMatches(mode, run.id) && (mode !== 'live' || router.state.location.pathname.endsWith('/operations'))) stage = 'done';
      else if (run?.status === 'Running') stage = 'running';
      else if (run && ['Failed', 'Succeeded', 'Cancelled'].includes(run.status)) stage = 'retry';
      else if (mode === 'build') {
        const workflow = world.workflows.find(entry => entry.id === MISSION_WORKFLOW_IDS.build);
        if (!workflow) stage = 'reset';
        else {
          const edges = definitionOf(workflow).edges;
          const connected = edges.some(edge => edge.source === 'trigger' && edge.target === 'script') && edges.some(edge => edge.source === 'script' && edge.target === 'result');
          stage = connected ? workflow.isEnabled ? 'run' : 'publish' : 'work';
        }
      }
    }
    render();
  }

  function render(): void {
    if (!active) return;
    const lang = demoLanguage();
    const copy = COPY[lang][mode];
    const ui = UI[lang];
    const focused = panel.contains(document.activeElement) ? (document.activeElement as HTMLElement).dataset.tourAction : undefined;
    panel.replaceChildren();
    panel.dataset.stage = stage;
    panel.dataset.tour = mode;
    panel.setAttribute('aria-label', copy.title);
    const header = text('div', '');
    header.className = 'np-tour-header';
    header.append(text('span', ui.badge), button('×', 'close', end, true));
    header.querySelector('button')?.setAttribute('aria-label', ui.close);
    const step = stage === 'done' ? 3 : ['answer', 'publish', 'running'].includes(stage) ? 2 : stage === 'run' ? 3 : 1;
    const progress = text('small', `${IDS.indexOf(mode) + 3} / 10 · ${lang === 'de' ? 'Schritt' : 'Step'} ${step} / 3`);
    progress.className = 'np-tour-progress';
    panel.append(header, progress, text('h2', copy.title));
    panel.append(text('p', stage === 'reset' ? ui.resetText : copy[stage] ?? copy.work));
    const target = mode === 'live' && stage === 'running' ? '/operations' : taskRoute(mode);
    if (stage === 'answer' && (mode === 'versions' || mode === 'machine')) {
      for (const [id, label] of ui.answers[mode]) panel.append(button(label, id, () => {
        if (id === (mode === 'versions' ? 'return' : 'none')) { stage = 'done'; wrong = false; }
        else wrong = true;
        render();
      }, true));
      if (wrong) { const feedback = text('p', ui.wrong); feedback.setAttribute('role', 'status'); panel.append(feedback); }
    }
    if (stage === 'done') {
      const run = findTaskRun(mode, baseline);
      if (run) panel.append(button(ui.result, 'result', () => navigate(`/executions?id=${encodeURIComponent(run.id)}`), true));
      panel.append(button(ui.explore, 'explore', end));
      const home = text('a', ui.website);
      home.href = '/'; home.target = '_blank'; home.rel = 'noopener noreferrer'; home.className = 'np-tour-quiet';
      panel.append(home);
    } else if (stage === 'reset') panel.append(button(ui.reset, 'reset', () => location.reload()));
    else if (mode === 'live' && stage === 'running' && !router.state.location.pathname.endsWith('/operations')) panel.append(button('Live Ops', 'open-ops', () => navigate('/operations')));
    else if (!router.state.location.pathname.endsWith(target)) panel.append(button(ui.resume, 'resume', () => navigate(target), true));
    const notice = text('small', ui.notice);
    notice.className = 'np-tour-notice';
    panel.append(notice);
    if (focused) panel.querySelector<HTMLElement>(`[data-tour-action="${focused}"]`)?.focus({ preventScroll: true });
  }

  function observeInterface(): void {
    if (!active) return;
    if (mode === 'versions' && router.state.location.pathname.includes(`/workflows/${FILE_WORKFLOW_ID}`)) {
      if (document.querySelector('.np-anim-backdrop button.bg-primary-fixed')) { seenVersion = true; update(); }
    }
    if (mode === 'machine' && router.state.location.pathname.endsWith('/machines')) {
      const inputs = [...document.querySelectorAll<HTMLInputElement>('#root input[type="text"]')];
      if (inputs.some(input => input.value.trim().toUpperCase() === 'LAB01') && document.getElementById('root')?.textContent?.includes('LAB01')) { seenMachine = true; update(); }
    }
  }

  function start(nextMode: NewMissionId): void {
    mode = nextMode;
    active = true;
    stage = 'work'; wrong = false; seenVersion = false; seenMachine = false;
    const world = getWorld();
    baseline = new Set(world.executions.map(run => run.id));
    const patch = world.maintenanceWindows.find(window => window.id === demoId('maintenance-window:patch-night'));
    initialWindowTime = `${patch?.weeklyStartMinuteOfDay}:${patch?.weeklyEndMinuteOfDay}`;
    if (mode in MISSION_WORKFLOW_IDS && !ensureMissionWorkflow(mode as keyof typeof MISSION_WORKFLOW_IDS)) stage = 'reset';
    if (mode in MISSION_WORKFLOW_IDS && stage !== 'reset') {
      const workflow = world.workflows.find(entry => entry.id === MISSION_WORKFLOW_IDS[mode as keyof typeof MISSION_WORKFLOW_IDS]);
      const definition = workflow && definitionOf(workflow);
      if (!definition || (mode === 'build' && (workflow?.isEnabled || definition.edges.length !== 0))
        || (['decision', 'parallel', 'service'].includes(mode) && !missionPlan(workflow!.id, definition.nodes, definition.edges, { freeSpaceGb: '8', serviceState: 'Stopped' }))) stage = 'reset';
    }
    if (mode === 'maintenance' && (!patch || initialWindowTime !== '1320:120' || patch.weeklyDaysMask !== 64 || patch.mode !== 'Blackout' || patch.scopeKind !== 'Folders' || !patch.targets.some(target => target.targetKind === 'Folder' && target.targetId === demoId('folder:operations')))) stage = 'reset';
    if (mode === 'machine') {
      const lab = world.machines.find(machine => machine.name === 'LAB01');
      if (!lab || lab.isReachable || lab.usedByWorkflowCount !== 0) stage = 'reset';
    }
    if (mode === 'versions') {
      const workflow = world.workflows.find(entry => entry.id === FILE_WORKFLOW_ID);
      const previous = world.versions.get(FILE_WORKFLOW_ID)?.find(version => !version.isCurrent);
      const currentHasResult = workflow && definitionOf(workflow).nodes.some(node => node.data?.activityType === 'returnData');
      const olderHasResult = previous && (JSON.parse(previous.definitionJson) as { nodes: { data?: { activityType?: string } }[] }).nodes.some(node => node.data?.activityType === 'returnData');
      if (!currentHasResult || !previous || olderHasResult) stage = 'reset';
    }
    panel.hidden = false;
    document.body.classList.add('np-tour-open');
    document.body.insertBefore(panel, document.getElementById('root'));
    navigate(taskRoute(mode));
    render();
  }

  const unsubscribe = subscribeWorld(update);
  const unsubscribeRouter = router.subscribe(() => { render(); observeInterface(); });
  const observer = new MutationObserver(observeInterface);
  const root = document.getElementById('root');
  if (root) observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
  document.addEventListener('input', observeInterface);
  const requested = new URLSearchParams(location.search).get('tour');
  if (isNewMissionId(requested)) start(requested);
  return { start, dispose() { unsubscribe(); unsubscribeRouter(); observer.disconnect(); document.removeEventListener('input', observeInterface); if (active) end(); panel.remove(); } };
}
