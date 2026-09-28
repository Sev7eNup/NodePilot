import { demoLanguage } from './strings';

const ORDER = ['build', 'diagnose', 'file', 'decision', 'parallel', 'service', 'live', 'versions', 'machine', 'maintenance'];

export function appendEditorHint(panel: HTMLElement): void {
  const de = demoLanguage() === 'de';
  const details = document.createElement('details');
  const summary = document.createElement('summary');
  summary.textContent = de ? 'Was bedeutet „produktiv“ in der Demo?' : 'What does “live” mean in this demo?';
  const explanation = document.createElement('p');
  explanation.textContent = de
    ? '„Produktiv“ heißt hier nur: Der Beispiel-Workflow ist veröffentlicht und bereit zum Starten. Alle Aktionen bleiben simuliert. „Bearbeiten“ reserviert den Workflow für Änderungen (Edit-Lock); „Veröffentlichen“ gibt ihn danach wieder für Starts frei.'
    : '“Live” only means the example workflow is published and ready to run. All actions remain simulated. “Edit” reserves the workflow for changes (edit lock); “Publish” makes it available for runs again.';
  details.append(summary, explanation);
  panel.append(details);
}

export function appendTourCompletion(panel: HTMLElement, mode: string): void {
  const de = demoLanguage() === 'de';
  const status = document.createElement('p');
  status.setAttribute('role', 'status');
  status.textContent = de ? '✓ Aufgabe abgeschlossen' : '✓ Task completed';
  panel.append(status);
  const next = ORDER[ORDER.indexOf(mode) + 1];
  if (!next) return;
  const link = document.createElement('a');
  link.className = 'np-tour-quiet';
  link.dataset.tourAction = 'next-task';
  link.href = `/demo/?tour=${next}&lang=${demoLanguage()}`;
  link.textContent = de ? 'Nächste Aufgabe' : 'Next task';
  panel.append(link);
}
