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

import { appendTourCompletion, appendEditorHint } from './tourNavigation';

type Stage = 'work' | 'publish' | 'run' | 'running' | 'answer' | 'retry' | 'done' | 'reset';
type MissionCopy = { title: string; work: string; publish?: string; run?: string; running?: string; inspect?: string; answer?: string; retry?: string; done: string };
const COPY: Record<'de' | 'en', Record<NewMissionId, MissionCopy>> = {
  de: {
    build: { title: 'Einen Workflow bauen', work: 'Die drei Schritte (Activities) sind vorkonfiguriert. Die roten Fehlerzahlen zeigen anfangs die noch fehlenden Verbindungen. Sie verschwinden nach dem Verbinden und Speichern. Bewege den Mauszeiger an den rechten Rand von „Manual Trigger“. Ziehe vom Anschluss zum linken Rand von „PowerShell“. Verbinde genauso PowerShell mit Return Data. Klicke oben auf das Diskettensymbol „Zwischen-Speichern“.', publish: 'Beide Verbindungen sind gespeichert. Klicke oben auf das Raketensymbol „Veröffentlichen“. Damit wird der Workflow für Ausführungen freigegeben.', run: 'Klicke oben auf das Play-Symbol „Ausführen“, um deinen Workflow zu starten.', running: 'Die Markierung zeigt, welcher Schritt gerade läuft. Danach kannst du das Ergebnis in Ruhe öffnen.', retry: 'Dieser Lauf war nicht erfolgreich. Öffne seine Ausführung, prüfe die Fehlermeldung und starte nach der Korrektur erneut über „Ausführen“.', done: 'Dein Workflow ist erfolgreich gelaufen. Klicke auf „Ausführung öffnen“ und suche bei „Return Data: greeting“ nach message. Dort sollte „Hello from NodePilot“ stehen.' },
    decision: { title: 'Einen Entscheidungspfad verfolgen', work: 'Klicke oben auf das Play-Symbol „Ausführen“. Trage im Dialog bei freeSpaceGb den Wert 8 ein und klicke auf „Ausführen“. Das simuliert 8 GB freien Speicher: unter 10 GB soll der kritische Zweig laufen.', running: 'Verfolge „Decision: severity“ und den Zweig „critical (<10 GB)“. Die anderen Zweige sollen nicht laufen. Das Ergebnis bleibt danach in der Ausführung sichtbar.', retry: 'Klicke erneut auf „Ausführen“ und starte mit freeSpaceGb = 8. Ein anderer Wert nimmt einen anderen Zweig.', done: 'Bei 8 GB lief der kritische Zweig mit Ordneranalyse und Warnmail. Öffne die Ausführung: Bei „Decision: severity“ steht branch = critical, bei „Return“ stehen freeSpaceGb = 8 und severity = critical.' },
    parallel: { title: 'Parallele Arbeit beobachten', work: 'Klicke oben auf „Ausführen“ und im Dialog auf „Ausführen“. Lass path und olderThanDays unverändert. Nach dem Archivieren starten Prüfsumme und Bereinigung gleichzeitig. Alle Dateiaktionen sind simuliert.', running: 'Beobachte die beiden Zweige nach „zipOperation“. „Junction: waitAll“ ist die Zusammenführung: Sie wartet, bis beide Zweige fertig sind, bevor die Abschlussmail folgt.', retry: 'Der Lauf wurde nicht vollständig abgeschlossen. Starte ihn über „Ausführen“ erneut mit den vorgeschlagenen Werten.', done: 'Beide Zweige liefen überlappend. Klicke hier auf „Gantt-Zeitleiste öffnen“. Du kannst die Ergebnisse dort ohne Zeitdruck vergleichen. Die Balken für checksum und remove überlappen; Junction und Email beginnen danach.' },
    service: { title: 'Einen Dienst wiederherstellen', work: 'Klicke oben auf „Ausführen“. Lass serviceState auf Stopped und bestätige mit „Ausführen“. Das simuliert einen gestoppten Druckwarteschlangendienst (Spooler). Der Workflow soll ihn starten und den Status erneut prüfen.', running: 'Verfolge „start spooler“, die Wartezeit „Delay“ und „verify spooler state“. Erst die erneute Prüfung bestätigt, ob der Dienst läuft.', retry: 'Klicke erneut auf „Ausführen“ und starte mit serviceState = Stopped. Bei Running ist keine Wiederherstellung nötig.', done: 'Der Dienst wurde gestartet und erneut geprüft. Öffne die Ausführung: „verify spooler state“ meldet Running. Erst danach folgt „Email: spooler recovered“. Eine erfolgreiche Startanforderung allein wäre noch keine Bestätigung.' },
    live: { title: 'Einen laufenden Workflow abbrechen', work: 'Klicke oben auf das Play-Symbol „Ausführen“. Der simulierte Prüfschritt bleibt für diese Übung aktiv, bis du ihn abbrichst. Du hast Zeit, die nächste Anweisung zu lesen.', running: 'Dein Lauf wartet im Prüfschritt. Klicke hier auf „Live Ops“, um ihn in der Zeitleiste zu finden.', inspect: 'Klicke in der Zeitleiste auf den laufenden Balken von „Live Ops Check“ – nicht auf die ID neben dem Namen. Klicke in den geöffneten Ausführungsdetails auf „Abbrechen“.', retry: 'Starte den Workflow erneut über „Ausführen“ und brich ihn in den Ausführungsdetails von Live Ops ab.', done: 'Der Status lautet „Abgebrochen“. Öffne die Ausführung: Der Prüfschritt wurde gestoppt, Return Data wurde nicht mehr ausgeführt. Der Workflow selbst bleibt aktiviert; nur dieser Lauf wurde beendet.' },
    versions: { title: 'Zwei Workflow-Versionen vergleichen', work: 'Klicke oben auf „…“ (Weitere Designer-Aktionen) und dann auf „Diff gegen vorherige Version“. Wähle links „Version 7“. „Hinzugefügt“ zeigt, was der aktuelle Workflow gegenüber dieser älteren Version enthält.', answer: 'Sieh im Vergleich unter „Nodes hinzugefügt“ nach. Welche Activity fehlt in Version 7? Du kannst die Antwort hier neben dem geöffneten Vergleich auswählen.', done: 'In Version 7 fehlt „Return Data: result“. Der Vergleich zählt zwei Ergänzungen: den Schritt und seine Verbindung von Log. Du hast nur verglichen; keine Version wurde wiederhergestellt.' },
    machine: { title: 'Eine Zielmaschine prüfen', work: 'Gib LAB01 in das Suchfeld direkt über der Maschinenliste ein. Lies die Spalten „Status“ und „Workflows“. 0 bei Workflows bedeutet: kein Workflow verwendet diese Maschine.', answer: 'Welche Aussage passt zur angezeigten Zeile von LAB01? „Unbekannt“ ist keine Bestätigung eines Ausfalls.', done: 'LAB01 hat den Status „Unbekannt“, und kein Workflow verwendet sie. Daraus lässt sich kein aktueller Ausfall ableiten. Im echten Betrieb würdest du mit „Verbindung testen“ die Erreichbarkeit prüfen.' },
    maintenance: { title: 'Ein Wartungsfenster planen', work: 'Klicke in der Zeile „Patch night“ auf das Stiftsymbol „Bearbeiten“. Ändere die Startzeit von 22:00 auf 23:00. Lass Samstag, Endzeit 02:00, Blackout und den Ordner /Operations ausgewählt. Speichere mit „Aktualisieren“.', done: 'Prüfe die Zeile „Patch night“: Samstag 23:00 bis Sonntag 02:00, Blackout für /Operations inklusive Unterordner. In dieser Zeit werden neue Starts gesperrt. Bereits laufende Ausführungen werden nicht abgebrochen.' },
  },
  en: {
    build: { title: 'Build a workflow', work: 'The three steps (activities) are configured for you. Hover over the right edge of Manual Trigger. Drag its connector to the left edge of PowerShell. Connect PowerShell to Return Data the same way. Click the disk icon “Save in place” in the toolbar.', publish: 'Both connections are saved. Click the rocket icon “Publish” in the toolbar to enable the workflow for execution.', run: 'Click the play icon “Run” in the toolbar to start your workflow.', running: 'The highlight shows which step is running. You can open the result afterwards at your own pace.', retry: 'This execution did not succeed. Open it, inspect the error, then correct the workflow and start another Run.', done: 'Your workflow succeeded. Click “Open execution” and find message under “Return Data: greeting”. It should say “Hello from NodePilot”.' },
    decision: { title: 'Follow a decision', work: 'Click the play icon “Run” in the toolbar. Enter 8 for freeSpaceGb in the dialog and click “Run”. This simulates 8 GB free: below 10 GB, the critical branch should run.', running: 'Follow “Decision: severity” and the “critical (<10 GB)” branch. The other branches should not run. You can inspect the execution afterwards.', retry: 'Click “Run” again and enter freeSpaceGb = 8. Other values take different branches.', done: 'At 8 GB, the critical branch ran with folder analysis and an alert email. Open the execution: “Decision: severity” reports branch = critical; “Return” reports freeSpaceGb = 8 and severity = critical.' },
    parallel: { title: 'Watch parallel work', work: 'Click “Run”, then “Run” in the dialog. Leave path and olderThanDays unchanged. After archiving, checksum and cleanup start together. All file actions are simulated.', running: 'Watch the two branches after zipOperation. “Junction: waitAll” joins them: it waits for both to finish before the summary email can run.', retry: 'This execution did not finish both branches. Start another run with the suggested values using the play button.', done: 'Both branches overlapped. Click “Open Gantt timeline” here to compare the results at your own pace. The checksum and remove bars overlap; Junction and Email follow them.' },
    service: { title: 'Recover a service', work: 'Click “Run”. Leave serviceState at Stopped and click “Run”. This simulates a stopped Print Spooler service. The workflow should start it and check its status again.', running: 'Follow “start spooler”, the Delay and “verify spooler state”. The second check confirms whether the service is running.', retry: 'Click “Run” again with serviceState = Stopped. Running does not need recovery.', done: 'The service was started and checked again. Open the execution: “verify spooler state” reports Running, followed by “Email: spooler recovered”. A successful start request alone would not confirm recovery.' },
    live: { title: 'Control a live run', work: 'Click the play icon “Run”. For this exercise, the simulated check stays active until you cancel it. Take your time reading the next instruction.', running: 'Your execution is waiting in the check step. Click “Live Ops” here to find it in the timeline.', inspect: 'In the timeline, click the running bar for “Live Ops Check”, not the ID beside its name. Click “Cancel” in the execution details that open.', retry: 'Start another run with the play button and cancel it from the execution details in Live Ops.', done: 'The status is “Cancelled”. Open the execution: the check was stopped and Return Data did not run. The workflow remains enabled; only this execution was cancelled.' },
    versions: { title: 'Compare two versions', work: 'Click “…” (More designer actions), then “Diff against a previous version”. Select “Version 7” on the left. “Added” shows what the current workflow contains compared with that older version.', answer: 'Look under “Nodes added” in the comparison. Which activity is missing from version 7? Select your answer here beside the open comparison.', done: 'Version 7 is missing “Return Data: result”. The diff counts two additions: the activity and its edge from Log. You compared versions without restoring either one.' },
    machine: { title: 'Inspect a target machine', work: 'Enter LAB01 in the search field directly above the machine list. Read “Status” and “Workflows”. 0 under Workflows means no workflow uses this machine.', answer: 'Which statement matches the LAB01 row? “Unknown” does not confirm an outage.', done: 'LAB01 has status “Unknown” and no workflow uses it. This does not establish a current outage. In a real environment, use “Test connection” to check reachability.' },
    maintenance: { title: 'Plan a maintenance window', work: 'Click the pencil icon “Edit” in the Patch night row. Change the start time from 22:00 to 23:00. Keep Saturday, end time 02:00, Blackout and /Operations selected. Save with “Update”.', done: 'Check the Patch night row: Saturday 23:00 to Sunday 02:00, Blackout for /Operations including subfolders. New starts are blocked during this window; executions already running are not cancelled.' },
  },
};

const UI = {
  de: { badge: 'GEFÜHRTE AUFGABE', close: 'Führung beenden', resume: 'Zur Aufgabe zurück', result: 'Ausführung öffnen', explore: 'Demo frei erkunden', website: 'Zur NodePilot-Website', overview: 'Alle zehn Aufgaben', reset: 'Demo zurücksetzen', resetText: 'Die Beispieldaten wurden in diesem Tab verändert. Setze die Demo zurück, um diese Aufgabe mit ihrem Ausgangszustand zu starten.', wrong: 'Prüfe die bezeichnete Zeile noch einmal: Entscheidend sind der angezeigte Status bzw. die hinzugefügte Activity.', notice: 'Simulierte Daten · Änderungen gelten nur in diesem Tab', answers: { versions: [['return', 'Return Data'], ['script', 'PowerShell'], ['copy', 'File Copy']], machine: [['none', 'Unbekannt · 0 Workflows'], ['used', 'Nicht erreichbar · mehrere Workflows'], ['online', 'Erreichbar · 0 Workflows']] } },
  en: { badge: 'GUIDED TASK', close: 'End walkthrough', resume: 'Return to the task', result: 'Open execution', explore: 'Explore the demo', website: 'Back to NodePilot', overview: 'All ten tasks', reset: 'Reset demo', resetText: 'The example data has changed in this tab. Reset the demo to start this task from its original state.', wrong: 'Check the indicated row again: look at the displayed status or the added activity.', notice: 'Simulated data · changes stay in this tab', answers: { versions: [['return', 'Return Data'], ['script', 'PowerShell'], ['copy', 'File Copy']], machine: [['none', 'Unknown · 0 workflows'], ['used', 'Unreachable · several workflows'], ['online', 'Reachable · 0 workflows']] } },
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
  let renderedState = '';
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
      if (patch?.isEnabled && initialWindowTime !== '1380:120' && patch.weeklyStartMinuteOfDay === 1380 && patch.weeklyEndMinuteOfDay === 120 && patch.weeklyDaysMask === 64 && patch.mode === 'Blackout' && patch.scopeKind === 'Folders' && patch.targets.length === 1 && patch.targets.some(target => target.targetKind === 'Folder' && target.targetId === operations)) stage = 'done';
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
    // World and interface observers also fire for unrelated updates. Keep buttons mounted
    // until their content changes so a pointer or keyboard action can finish reliably.
    const state = JSON.stringify([mode, stage, lang, wrong, router.state.location.pathname, router.state.location.search, findTaskRun(mode, baseline)?.id]);
    if (state === renderedState) return;
    renderedState = state;
    const focused = panel.contains(document.activeElement) ? (document.activeElement as HTMLElement).dataset.tourAction : undefined;
    panel.replaceChildren();
    panel.dataset.stage = stage;
    panel.dataset.tour = mode;
    panel.setAttribute('aria-label', copy.title);
    const header = text('div', '');
    header.className = 'np-tour-header';
    header.append(text('span', ui.badge), button('×', 'close', end, true));
    header.querySelector('button')?.setAttribute('aria-label', ui.close);
    const total = mode === 'build' ? 4 : mode === 'maintenance' ? 2 : 3;
    const step = stage === 'done' ? total : mode === 'build' && ['run', 'running', 'retry'].includes(stage) ? 3 : ['answer', 'publish', 'running'].includes(stage) ? 2 : 1;
    const task = mode === 'build' ? 1 : IDS.indexOf(mode) + 3;
    const progress = text('small', `${lang === 'de' ? 'Aufgabe' : 'Task'} ${task} / 10 · ${lang === 'de' ? 'Schritt' : 'Step'} ${step} / ${total}`);
    progress.className = 'np-tour-progress';
    panel.append(header, progress, text('h2', copy.title));
    const instruction = stage === 'reset' ? ui.resetText : mode === 'live' && stage === 'running' && router.state.location.pathname.endsWith('/operations') ? copy.inspect : copy[stage];
    panel.append(text('p', instruction ?? copy.work));
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
      appendTourCompletion(panel, mode);
      const run = findTaskRun(mode, baseline);
      if (mode === 'parallel' && run) panel.append(button(lang === 'de' ? 'Gantt-Zeitleiste öffnen' : 'Open Gantt timeline', 'gantt', () => navigate(`${taskRoute(mode)}?historyExecution=${encodeURIComponent(run.id)}&historyView=gantt`)));
      if (run) panel.append(button(ui.result, 'result', () => navigate(`/executions?id=${encodeURIComponent(run.id)}`), true));
      panel.append(button(ui.explore, 'explore', end));
      const home = text('a', ui.website);
      home.href = '/'; home.target = '_blank'; home.rel = 'noopener noreferrer'; home.className = 'np-tour-quiet';
      panel.append(home);
      const overview = text('a', ui.overview);
      overview.href = lang === 'de' ? '/walkthrough/' : '/en/walkthrough/';
      overview.className = 'np-tour-quiet';
      panel.append(overview);
    } else if (stage === 'reset') panel.append(button(ui.reset, 'reset', () => location.reload()));
    else if (mode === 'live' && stage === 'running' && !router.state.location.pathname.endsWith('/operations')) panel.append(button('Live Ops', 'open-ops', () => navigate('/operations')));
    else if (!router.state.location.pathname.endsWith(target)) panel.append(button(ui.resume, 'resume', () => navigate(target), true));
    const notice = text('small', ui.notice);
    if (stage === 'work' && mode in MISSION_WORKFLOW_IDS) appendEditorHint(panel);
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
    renderedState = '';
    active = true;
    stage = 'work'; wrong = false; seenVersion = false; seenMachine = false;
    const world = getWorld();
    baseline = new Set(world.executions.map(run => run.id));
    const patch = world.maintenanceWindows.find(window => window.id === demoId('maintenance-window:patch-night'));
    initialWindowTime = `${patch?.weeklyStartMinuteOfDay}:${patch?.weeklyEndMinuteOfDay}`;
    if (mode in MISSION_WORKFLOW_IDS && !ensureMissionWorkflow(mode as keyof typeof MISSION_WORKFLOW_IDS, demoLanguage())) stage = 'reset';
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
  const resize = new ResizeObserver(() => document.body.style.setProperty('--np-tour-height', `${panel.getBoundingClientRect().height}px`));
  resize.observe(panel);
  const unsubscribeRouter = router.subscribe(() => { render(); observeInterface(); });
  const observer = new MutationObserver(observeInterface);
  const root = document.getElementById('root');
  if (root) observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
  document.addEventListener('input', observeInterface);
  const requested = new URLSearchParams(location.search).get('tour');
  if (isNewMissionId(requested)) start(requested);
  return { start, dispose() { unsubscribe(); unsubscribeRouter(); observer.disconnect(); resize.disconnect(); document.removeEventListener('input', observeInterface); if (active) end(); panel.remove(); } };
}
