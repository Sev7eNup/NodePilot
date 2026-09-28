import { FILE_WORKFLOW_ID } from '../run/fileScenario';
import { getWorld, subscribeWorld } from '../state/world';
import { demoLanguage } from './strings';
import './tour.css';
import { MOBILE_BREAKPOINT } from '../../src/hooks/useMediaQuery';
import type { createBrowserRouter } from 'react-router';
import { mountAdditionalTours, isNewMissionId } from './additionalTours';
import type { NewMissionId } from '../seed/missionFixtures';
import { appendTourCompletion, appendEditorHint } from './tourNavigation';

type Stage = 'start' | 'running' | 'success' | 'result' | 'case' | 'diagnose' | 'done' | 'failed';
const COPY = {
  de: {
    title: 'Dein erster Workflow', close: 'Führung beenden', badge: 'GEFÜHRTE DEMO · 2 MINUTEN',
    notice: 'Du bedienst die echte Oberfläche mit simulierten Daten. Es werden keine echten Dateien geschrieben.',
    startTitle: '1. Deine Datei bereitstellen', start: 'Klicke oben auf das Play-Symbol „Ausführen“. Ändere im Dialog pollIntervalSeconds (das Prüfintervall in Sekunden) von 30 auf 60. Lass fileName = settings.json und environment = Test unverändert und klicke auf „Ausführen“.',
    mobileStart: 'Suche „Configuration File Delivery“ in der Workflow-Liste und tippe auf dessen Play-Symbol („Jetzt ausführen“). Ändere pollIntervalSeconds auf 60 und starte den Lauf.',
    goal: 'Ziel: Eine JSON-Konfiguration erzeugen und in den Zielordner kopieren.',
    runningTitle: 'Dein Workflow läuft', running: 'Verfolge die Activities auf dem Canvas. Eingaben und Ausgaben gehören zu diesem Lauf und bleiben anschließend in der Historie erhalten.',
    successTitle: 'Die Datei ist bereit', success: 'Alle Schritte sind abgeschlossen. Öffne jetzt das Ergebnis und prüfe, ob dein Intervall in der erzeugten Konfiguration steht.',
    resultTitle: 'Datei bereitgestellt', result: 'Scrolle in der aufgeklappten Ausführung zur letzten Zeile „Return Data: result“. Unter content (Dateiinhalt) steht pollIntervalSeconds mit deinem gewählten Wert; destination ist der Zielpfad der kopierten Datei. Damit ist die Dateibereitstellung abgeschlossen.',
    openResult: 'Mein Ergebnis öffnen', next: 'Weiter: Einen Fehler untersuchen', openWorkflow: 'Zum Beispiel-Workflow',
    caseTitle: 'Warum wurde die Datei nicht kopiert?', case: 'Ein vorbereiteter Lauf desselben Workflows ist fehlgeschlagen. Untersuche seine Schritte und das Fehlerprotokoll.',
    openFailure: 'Fehlgeschlagenen Lauf öffnen', diagnoseTitle: '3. Finde die Ursache', diagnose: 'Suche die erste fehlgeschlagene Activity. Vergleiche ihre Meldung mit den erfolgreichen Schritten davor. Was verhindert das Kopieren?',
    permission: 'Keine Schreibberechtigung im Zielordner', script: 'PowerShell konnte keine Datei erzeugen', service: 'Der Dienst wurde nicht gefunden',
    wrong: 'Schau noch einmal auf die erfolgreichen Schritte vor File Copy und auf die Meldung „Access denied“.',
    doneTitle: 'Ursache gefunden', done: 'PowerShell hat die Datei erzeugt. File Copy scheitert an den Schreibrechten im Zielordner unter System32. Prüfe Zielpfad und Berechtigungen des Ausführungskontos. Ein erneuter Lauf ohne Änderung würde wieder scheitern.',
    takeaway: 'Du hast einen Workflow gestartet, seine Ausgaben geprüft und einen Fehler auf die betroffene Activity eingegrenzt.',
    directTitle: 'Finde die Ursache', directDiagnose: 'Dieser vorbereitete Lauf ist fehlgeschlagen. Suche in der Tabelle den roten Schritt „File Copy: configuration“. Vergleiche seine Fehlermeldung mit dem erfolgreichen PowerShell-Schritt davor. Warum konnte die Datei nicht kopiert werden?',
    directTakeaway: 'Du hast einen vorhandenen Fehler untersucht und die Ursache anhand der Schritt-Ergebnisse eingegrenzt.', overview: 'Alle zehn Aufgaben',
    website: 'Zurück zur NodePilot-Website', explore: 'Demo frei erkunden', restart: 'Datei-Beispiel ausprobieren', failedTitle: 'Dieser Lauf ist nicht erfolgreich', failed: 'Öffne die Ausführung und prüfe ihre Fehlermeldung. Du kannst danach zum Workflow zurückkehren und mit environment = Test erneut starten.',
    progress: 'Activities abgeschlossen', resume: 'Zur Aufgabe zurück',
  },
  en: {
    title: 'Your first workflow', close: 'End walkthrough', badge: 'GUIDED DEMO · 2 MINUTES',
    notice: 'You are using the real interface with simulated data. No real files are written.',
    startTitle: '1. Deliver your file', start: 'Click the play icon “Run” in the toolbar. In the dialog, change pollIntervalSeconds (the check interval in seconds) from 30 to 60. Leave fileName = settings.json and environment = Test unchanged, then click “Run”.',
    mobileStart: 'Find “Configuration File Delivery” in the workflow list and tap its Play icon (“Run Now”). Change pollIntervalSeconds to 60 and start the execution.',
    goal: 'Goal: create a JSON configuration and copy it to its destination.',
    runningTitle: 'Your workflow is running', running: 'Follow the activities on the canvas. Inputs and outputs belong to this execution and remain available in its history.',
    successTitle: 'Your file is ready', success: 'Every step has completed. Open the result and check whether your interval appears in the generated configuration.',
    resultTitle: 'File delivered', result: 'Scroll to the last row, “Return Data: result”, in the expanded execution. Under content (the file contents), pollIntervalSeconds should match your chosen value; destination is the copied file’s target path. This completes the file delivery task.',
    openResult: 'Open my result', next: 'Next: Investigate a failure', openWorkflow: 'Open example workflow',
    caseTitle: 'Why was the file not copied?', case: 'A prepared execution of the same workflow has failed. Inspect its steps and error log.',
    openFailure: 'Open failed execution', diagnoseTitle: '3. Find the cause', diagnose: 'Find the first failed activity. Compare its message with the successful steps before it. What prevents the copy?',
    permission: 'No write permission on the destination', script: 'PowerShell could not create the file', service: 'The service was not found',
    wrong: 'Look again at the successful steps before File Copy and the “Access denied” message.',
    doneTitle: 'You found the cause', done: 'PowerShell created the file. File Copy fails because the execution account cannot write to the destination under System32. Check the destination and account permissions. Retrying without a change would fail again.',
    takeaway: 'You started a workflow, checked its outputs and traced a failure to the activity responsible.',
    directTitle: 'Find the cause', directDiagnose: 'This prepared execution failed. Find the red “File Copy: configuration” step in the table. Compare its error with the successful PowerShell step before it. Why could the file not be copied?',
    directTakeaway: 'You investigated an existing failure and traced its cause using the step results.', overview: 'All ten tasks',
    website: 'Back to the NodePilot website', explore: 'Explore the demo', restart: 'Try the file example', failedTitle: 'This execution did not succeed', failed: 'Open the execution and inspect its error. You can then return to the workflow and run again with environment = Test.',
    progress: 'activities completed', resume: 'Return to the task',
  },
};

export function mountTour(router: ReturnType<typeof createBrowserRouter>): { start(mode?: 'file' | 'diagnose' | NewMissionId): void; dispose(): void } {
  const additional = mountAdditionalTours(router);
  const panel = document.createElement('aside');
  panel.className = 'np-tour'; panel.hidden = true;
  panel.setAttribute('aria-label', COPY[demoLanguage()].title);
  let stage: Stage = 'start';
  let active = false;
  let baseline = new Set<string>();
  let executionId: string | null = null;
  let feedback = false;
  let activeMode: 'file' | 'diagnose' = 'file';
  const requestedLanguage = new URLSearchParams(location.search).get('lang');
  const workflowRoute = `/workflows/${FILE_WORKFLOW_ID}`;
  const mobile = window.matchMedia(MOBILE_BREAKPOINT);
  const startRoute = () => mobile.matches ? '/workflows' : workflowRoute;
  const failure = () => getWorld().executions.find(run => run.workflowId === FILE_WORKFLOW_ID && run.status === 'Failed' && baseline.has(run.id));
  const navigate = (path: string, replace = false) => {
    const target = new URL(path, location.origin);
    const current = new URLSearchParams(router.state.location.search);
    for (const key of ['lang', 'tour']) {
      const value = current.get(key);
      if (value && !target.searchParams.has(key)) target.searchParams.set(key, value);
    }
    if (active) target.searchParams.set('tour', activeMode);
    if (requestedLanguage && !target.searchParams.has('lang')) target.searchParams.set('lang', requestedLanguage);
    void router.navigate(target.pathname + target.search, { replace });
  };
  const resultRoute = (id: string) => `/executions?id=${encodeURIComponent(id)}`;
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
    const url = new URL(location.href); url.searchParams.delete('tour');
    void router.navigate(url.pathname.slice('/demo'.length) + url.search, { replace: true });
  }
  function showFailure() {
    const run = failure();
    if (!run) return;
    stage = 'diagnose'; feedback = false; navigate(resultRoute(run.id)); render();
  }
  function render() {
    if (!active) return;
    document.body.classList.toggle('np-tour-execution', router.state.location.pathname === '/demo/executions');
    const copy = COPY[demoLanguage()];
    const focusedAction = panel.contains(document.activeElement) ? (document.activeElement as HTMLElement)?.dataset.tourAction : undefined;
    panel.replaceChildren(); panel.dataset.stage = stage; panel.dataset.tour = activeMode;
    const header = text('div', ''); header.className = 'np-tour-header';
    header.append(text('span', copy.badge), button('×', 'close', end, true));
    header.querySelector('button')!.setAttribute('aria-label', copy.close);
    const progressLabel = text('small', `${demoLanguage() === 'de' ? 'Aufgabe' : 'Task'} ${activeMode === 'diagnose' ? 2 : 3} / 10`);
    progressLabel.className = 'np-tour-progress';
    panel.append(header, progressLabel);
    const directDiagnosis = activeMode === 'diagnose' && stage === 'diagnose';
    panel.append(text('h2', directDiagnosis ? copy.directTitle : copy[`${stage}Title`]), text('p', directDiagnosis ? copy.directDiagnose : stage === 'start' && mobile.matches ? copy.mobileStart : copy[stage]));
    const current = getWorld().executions.find(run => run.id === executionId);
    if (stage === 'start') { panel.append(text('div', copy.goal)); appendEditorHint(panel); }
    if (stage === 'running' && current) {
      const progress = document.createElement('progress'); progress.max = current.stepsTotal ?? 7; progress.value = current.stepsCompleted ?? 0;
      progress.setAttribute('aria-label', copy.progress);
      panel.append(progress, text('small', `${progress.value} / ${progress.max} ${copy.progress}`));
    }
    if ((stage === 'success' || stage === 'failed') && executionId) panel.append(button(copy.openResult, 'result', () => { stage = current?.status === 'Succeeded' ? 'result' : 'failed'; navigate(resultRoute(executionId!)); render(); }));
    if (stage === 'result') {
      appendTourCompletion(panel, 'file');
      panel.append(button(demoLanguage() === 'de' ? 'Optional: Fehleranalyse üben' : 'Optional: investigate a failure', 'failure', () => start('diagnose'), true));
      panel.append(button(copy.explore, 'explore', end, true));
    }
    if (stage === 'case') panel.append(button(copy.openFailure, 'failure', showFailure));
    if (stage === 'diagnose') {
      for (const choice of ['script', 'permission', 'service'] as const) panel.append(button(copy[choice], choice, () => {
        if (choice === 'permission') stage = 'done'; else feedback = true;
        render();
      }, true));
      if (feedback) { const message = text('p', copy.wrong); message.setAttribute('role', 'status'); panel.append(message); }
    }
    if (stage === 'done') {
      appendTourCompletion(panel, activeMode);
      // The tour covers the demo banner, so its own way back to the project website belongs
      // here — a visitor who came straight into the tour has none otherwise.
      const home = text('a', copy.website);
      home.href = '/';
      home.target = '_blank';
      home.rel = 'noopener noreferrer';
      home.dataset.tourAction = 'website';
      home.className = 'np-tour-quiet';
      panel.append(
        text('p', activeMode === 'diagnose' ? copy.directTakeaway : copy.takeaway),
        button(copy.explore, 'explore', end),
        button(copy.restart, 'restart', () => start('file'), true),
        home,
      );
      const overview = text('a', copy.overview);
      overview.href = demoLanguage() === 'de' ? '/walkthrough/' : '/en/walkthrough/';
      overview.className = 'np-tour-quiet';
      panel.append(overview);
    } else if (stage === 'start' || stage === 'failed') panel.append(button(copy.openWorkflow, 'workflow', () => { stage = 'start'; navigate(startRoute()); render(); }, true));
    const expected = stage === 'diagnose' || stage === 'done' ? failure()?.id : executionId;
    const route = ['result', 'diagnose', 'done'].includes(stage) && expected ? resultRoute(expected) : stage === 'start' ? startRoute() : workflowRoute;
    const expectedUrl = new URL(`/demo${route}`, location.origin);
    const currentUrl = new URL(location.href);
    const atTask = currentUrl.pathname === expectedUrl.pathname && currentUrl.searchParams.get('id') === expectedUrl.searchParams.get('id');
    if (!atTask && !['success', 'failed'].includes(stage)) panel.append(button(copy.resume, 'resume', () => navigate(route), true));
    const notice = text('small', copy.notice); notice.className = 'np-tour-notice'; panel.append(notice);
    if (focusedAction) panel.querySelector<HTMLElement>(`[data-tour-action="${focusedAction}"]`)?.focus({ preventScroll: true });
  }
  function start(mode: 'file' | 'diagnose' = 'file') {
    activeMode = mode;
    baseline = new Set(getWorld().executions.map(run => run.id)); executionId = null; feedback = false;
    active = true; panel.hidden = false; document.body.classList.add('np-tour-open');
    document.body.insertBefore(panel, document.getElementById('root'));
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
  const unsubscribeRouter = router.subscribe(render);
  const requested = new URLSearchParams(location.search).get('tour');
  if (requested === 'file' || requested === 'diagnose') start(requested);
  return { start(mode = 'file') { if (isNewMissionId(mode)) additional.start(mode); else start(mode); }, dispose() { unsubscribeRouter(); end(); unsubscribe(); resize.disconnect(); panel.remove(); additional.dispose(); } };
}
