import { FILE_WORKFLOW_ID } from '../run/fileScenario';
import { getWorld, subscribeWorld } from '../state/world';
import { demoLanguage } from './strings';
import './tour.css';
import { MOBILE_BREAKPOINT } from '../../src/hooks/useMediaQuery';

type Stage = 'start' | 'running' | 'success' | 'result' | 'case' | 'diagnose' | 'done' | 'failed';
const COPY = {
  de: {
    title: 'Dein erster Workflow', close: 'Führung beenden', badge: 'GEFÜHRTE DEMO · 2 MINUTEN',
    notice: 'Du bedienst die echte Oberfläche mit simulierten Daten. Es werden keine echten Dateien geschrieben.',
    startTitle: '1. Deine Datei bereitstellen', start: 'Klicke oben auf „Ausführen“. Ändere im Startdialog pollIntervalSeconds von 30 auf 60 und starte den Lauf. Die anderen Werte kannst du lassen.',
    mobileStart: 'Suche „Configuration File Delivery“ in der Workflow-Liste und tippe auf dessen Play-Symbol („Jetzt ausführen“). Ändere pollIntervalSeconds auf 60 und starte den Lauf.',
    goal: 'Ziel: Eine JSON-Konfiguration erzeugen und in den Zielordner kopieren.',
    runningTitle: 'Dein Workflow läuft', running: 'Verfolge die Activities auf dem Canvas. Eingaben und Ausgaben gehören zu diesem Lauf und bleiben anschließend in der Historie erhalten.',
    successTitle: 'Die Datei ist bereit', success: 'Alle Schritte sind abgeschlossen. Öffne jetzt das Ergebnis und prüfe, ob dein Intervall in der erzeugten Konfiguration steht.',
    resultTitle: '2. Dein Ergebnis nachvollziehen', result: 'In der aufgeklappten Ausführung siehst du die Ausgaben jeder Activity. Suche bei „Return Data: result“ nach content und destination. Im Dateiinhalt steht dein gewähltes Intervall.',
    openResult: 'Mein Ergebnis öffnen', next: 'Weiter: Einen Fehler untersuchen', openWorkflow: 'Zum Beispiel-Workflow',
    caseTitle: 'Warum wurde die Datei nicht kopiert?', case: 'Ein vorbereiteter Lauf desselben Workflows ist fehlgeschlagen. Untersuche seine Schritte und das Fehlerprotokoll.',
    openFailure: 'Fehlgeschlagenen Lauf öffnen', diagnoseTitle: '3. Finde die Ursache', diagnose: 'Suche die erste fehlgeschlagene Activity. Vergleiche ihre Meldung mit den erfolgreichen Schritten davor. Was verhindert das Kopieren?',
    permission: 'Keine Schreibberechtigung im Zielordner', script: 'PowerShell konnte keine Datei erzeugen', service: 'Der Dienst wurde nicht gefunden',
    wrong: 'Schau noch einmal auf die erfolgreichen Schritte vor File Copy und auf die Meldung „Access denied“.',
    doneTitle: 'Ursache gefunden', done: 'PowerShell hat die Datei erzeugt. File Copy scheitert an den Schreibrechten im Zielordner unter System32. Prüfe Zielpfad und Berechtigungen des Ausführungskontos. Ein erneuter Lauf ohne Änderung würde wieder scheitern.',
    takeaway: 'Du hast einen Workflow gestartet, seine Ausgaben geprüft und einen Fehler auf die betroffene Activity eingegrenzt.',
    website: 'Zurück zur NodePilot-Website', explore: 'Demo frei erkunden', restart: 'Datei-Beispiel ausprobieren', failedTitle: 'Dieser Lauf ist nicht erfolgreich', failed: 'Öffne die Ausführung und prüfe ihre Fehlermeldung. Du kannst danach zum Workflow zurückkehren und mit environment = Test erneut starten.',
    progress: 'Activities abgeschlossen', resume: 'Zur Aufgabe zurück',
  },
  en: {
    title: 'Your first workflow', close: 'End walkthrough', badge: 'GUIDED DEMO · 2 MINUTES',
    notice: 'You are using the real interface with simulated data. No real files are written.',
    startTitle: '1. Deliver your file', start: 'Click “Run” at the top. In the start dialog, change pollIntervalSeconds from 30 to 60 and start the execution. Leave the other values as they are.',
    mobileStart: 'Find “Configuration File Delivery” in the workflow list and tap its Play icon (“Run Now”). Change pollIntervalSeconds to 60 and start the execution.',
    goal: 'Goal: create a JSON configuration and copy it to its destination.',
    runningTitle: 'Your workflow is running', running: 'Follow the activities on the canvas. Inputs and outputs belong to this execution and remain available in its history.',
    successTitle: 'Your file is ready', success: 'Every step has completed. Open the result and check whether your interval appears in the generated configuration.',
    resultTitle: '2. Understand your result', result: 'The expanded execution shows the output of each activity. Find content and destination under “Return Data: result”. The file content contains your chosen interval.',
    openResult: 'Open my result', next: 'Next: Investigate a failure', openWorkflow: 'Open example workflow',
    caseTitle: 'Why was the file not copied?', case: 'A prepared execution of the same workflow has failed. Inspect its steps and error log.',
    openFailure: 'Open failed execution', diagnoseTitle: '3. Find the cause', diagnose: 'Find the first failed activity. Compare its message with the successful steps before it. What prevents the copy?',
    permission: 'No write permission on the destination', script: 'PowerShell could not create the file', service: 'The service was not found',
    wrong: 'Look again at the successful steps before File Copy and the “Access denied” message.',
    doneTitle: 'You found the cause', done: 'PowerShell created the file. File Copy fails because the execution account cannot write to the destination under System32. Check the destination and account permissions. Retrying without a change would fail again.',
    takeaway: 'You started a workflow, checked its outputs and traced a failure to the activity responsible.',
    website: 'Back to the NodePilot website', explore: 'Explore the demo', restart: 'Try the file example', failedTitle: 'This execution did not succeed', failed: 'Open the execution and inspect its error. You can then return to the workflow and run again with environment = Test.',
    progress: 'activities completed', resume: 'Return to the task',
  },
};

export function mountTour(): { start(mode?: 'file' | 'diagnose'): void; dispose(): void } {
  const panel = document.createElement('aside');
  panel.className = 'np-tour'; panel.hidden = true;
  panel.setAttribute('aria-label', COPY[demoLanguage()].title);
  let stage: Stage = 'start';
  let active = false;
  let baseline = new Set<string>();
  let executionId: string | null = null;
  let feedback = false;
  const workflowRoute = `#/workflows/${FILE_WORKFLOW_ID}`;
  const mobile = window.matchMedia(MOBILE_BREAKPOINT);
  const startRoute = () => mobile.matches ? '#/workflows' : workflowRoute;
  const failure = () => getWorld().executions.find(run => run.workflowId === FILE_WORKFLOW_ID && run.status === 'Failed' && baseline.has(run.id));
  const navigate = (hash: string) => { location.hash = hash; };
  const resultRoute = (id: string) => `#/executions?id=${encodeURIComponent(id)}`;
  const text = <K extends keyof HTMLElementTagNameMap>(tag: K, value: string) => {
    const element = document.createElement(tag); element.textContent = value; return element;
  };
  function button(label: string, action: string, callback: () => void, quiet = false) {
    const node = text('button', label); node.type = 'button'; node.dataset.tourAction = action;
    if (quiet) node.className = 'np-tour-quiet';
    node.addEventListener('click', callback); return node;
  }
  function end() {
    active = false; panel.hidden = true; document.body.classList.remove('np-tour-open');
    document.body.classList.remove('np-tour-execution');
    panel.remove();
    const url = new URL(location.href); url.searchParams.delete('tour'); history.replaceState(null, '', url);
  }
  function showFailure() {
    const run = failure();
    if (!run) return;
    stage = 'diagnose'; feedback = false; navigate(resultRoute(run.id)); render();
  }
  function render() {
    if (!active) return;
    document.body.classList.toggle('np-tour-execution', location.hash.startsWith('#/executions'));
    const copy = COPY[demoLanguage()];
    const focusedAction = panel.contains(document.activeElement) ? (document.activeElement as HTMLElement)?.dataset.tourAction : undefined;
    panel.replaceChildren(); panel.dataset.stage = stage;
    const header = text('div', ''); header.className = 'np-tour-header';
    header.append(text('span', copy.badge), button('×', 'close', end, true));
    header.querySelector('button')!.setAttribute('aria-label', copy.close);
    panel.append(header, text('h2', copy[`${stage}Title`]), text('p', stage === 'start' && mobile.matches ? copy.mobileStart : copy[stage]));
    const current = getWorld().executions.find(run => run.id === executionId);
    if (stage === 'start') panel.append(text('div', copy.goal));
    if (stage === 'running' && current) {
      const progress = document.createElement('progress'); progress.max = current.stepsTotal ?? 7; progress.value = current.stepsCompleted ?? 0;
      progress.setAttribute('aria-label', copy.progress);
      panel.append(progress, text('small', `${progress.value} / ${progress.max} ${copy.progress}`));
    }
    if ((stage === 'success' || stage === 'failed') && executionId) panel.append(button(copy.openResult, 'result', () => { stage = current?.status === 'Succeeded' ? 'result' : 'failed'; navigate(resultRoute(executionId!)); render(); }));
    if (stage === 'result') panel.append(button(copy.next, 'next', () => { stage = 'case'; navigate(workflowRoute); render(); }));
    if (stage === 'case') panel.append(button(copy.openFailure, 'failure', showFailure));
    if (stage === 'diagnose') {
      for (const choice of ['script', 'permission', 'service'] as const) panel.append(button(copy[choice], choice, () => {
        if (choice === 'permission') stage = 'done'; else feedback = true;
        render();
      }, true));
      if (feedback) { const message = text('p', copy.wrong); message.setAttribute('role', 'status'); panel.append(message); }
    }
    if (stage === 'done') {
      // The tour covers the demo banner, so its own way back to the project website belongs
      // here — a visitor who came straight into the tour has none otherwise.
      const home = text('a', copy.website);
      home.href = '../';
      home.dataset.tourAction = 'website';
      home.className = 'np-tour-quiet';
      panel.append(
        text('p', copy.takeaway),
        button(copy.explore, 'explore', end),
        button(copy.restart, 'restart', () => start('file'), true),
        home,
      );
    } else if (stage === 'start' || stage === 'failed') panel.append(button(copy.openWorkflow, 'workflow', () => { stage = 'start'; navigate(startRoute()); render(); }, true));
    const expected = stage === 'diagnose' || stage === 'done' ? failure()?.id : executionId;
    const route = ['result', 'diagnose', 'done'].includes(stage) && expected ? resultRoute(expected) : stage === 'start' ? startRoute() : workflowRoute;
    if (location.hash !== route && !['success', 'failed'].includes(stage)) panel.append(button(copy.resume, 'resume', () => navigate(route), true));
    const notice = text('small', copy.notice); notice.className = 'np-tour-notice'; panel.append(notice);
    if (focusedAction) panel.querySelector<HTMLElement>(`[data-tour-action="${focusedAction}"]`)?.focus({ preventScroll: true });
  }
  function start(mode: 'file' | 'diagnose' = 'file') {
    baseline = new Set(getWorld().executions.map(run => run.id)); executionId = null; feedback = false;
    active = true; panel.hidden = false; document.body.classList.add('np-tour-open');
    document.body.insertBefore(panel, document.getElementById('root'));
    const url = new URL(location.href); url.searchParams.set('tour', mode); history.replaceState(null, '', url);
    stage = mode === 'diagnose' ? 'diagnose' : 'start';
    if (mode === 'diagnose') showFailure(); else { navigate(startRoute()); render(); }
  }
  const unsubscribe = subscribeWorld(() => {
    if (!active || !['start', 'running', 'success', 'failed'].includes(stage)) return;
    const current = getWorld().executions.find(run => run.workflowId === FILE_WORKFLOW_ID && !baseline.has(run.id));
    if (!current) return;
    if (stage === 'start' && mobile.matches && current.status === 'Running') navigate(workflowRoute);
    executionId = current.id;
    stage = current.status === 'Running' ? 'running' : current.status === 'Succeeded' ? 'success' : 'failed';
    render();
  });
  const resize = new ResizeObserver(() => document.body.style.setProperty('--np-tour-height', `${panel.getBoundingClientRect().height}px`));
  resize.observe(panel);
  window.addEventListener('hashchange', render);
  const requested = new URLSearchParams(location.search).get('tour');
  if (requested === 'file' || requested === 'diagnose') start(requested);
  return { start, dispose() { end(); unsubscribe(); resize.disconnect(); window.removeEventListener('hashchange', render); panel.remove(); } };
}
