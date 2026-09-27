import type { Lang } from '../../i18n/languages'

export const additionalMissions = {
  de: [
    { id: 'build', title: 'Einen Workflow bauen.', text: 'Drei Activities verbinden, veröffentlichen und den eigenen Lauf prüfen.', steps: ['Activities verbinden', 'Workflow veröffentlichen', 'Ergebnis prüfen'], duration: '4 Minuten' },
    { id: 'decision', title: 'Einer Entscheidung folgen.', text: 'Bei 8 GB freiem Speicher den kritischen Pfad der Speicherplatzprüfung verfolgen.', steps: ['8 GB eingeben', 'Kritischen Pfad starten', 'Ergebnis prüfen'], duration: '3 Minuten' },
    { id: 'parallel', title: 'Parallele Arbeit beobachten.', text: 'Prüfsumme und Bereinigung laufen vor der gemeinsamen Abschlussmail.', steps: ['Bereinigung starten', 'Beide Zweige verfolgen', 'Junction prüfen'], duration: '3 Minuten' },
    { id: 'service', title: 'Einen Dienst wiederherstellen.', text: 'Einen gestoppten Dienst starten und seine erneute Prüfung nachvollziehen.', steps: ['Gestoppten Dienst starten', 'Erneute Prüfung verfolgen', 'Meldung prüfen'], duration: '3 Minuten' },
    { id: 'live', title: 'Einen Lauf live steuern.', text: 'Eine laufende Ausführung in Live Ops finden und gezielt abbrechen.', steps: ['Lauf starten', 'In Live Ops öffnen', 'Abbruch prüfen'], duration: '2 Minuten' },
    { id: 'versions', title: 'Zwei Versionen vergleichen.', text: 'Im Diff entdecken, welche Activity der früheren Version fehlt.', steps: ['Versionsvergleich öffnen', 'Ältere Version wählen', 'Unterschied benennen'], duration: '2 Minuten' },
    { id: 'machine', title: 'Eine Zielmaschine prüfen.', text: 'LAB01 finden und Erreichbarkeit sowie Workflow-Nutzung einordnen.', steps: ['LAB01 filtern', 'Status und Nutzung lesen', 'Auswirkung benennen'], duration: '2 Minuten' },
    { id: 'maintenance', title: 'Ein Wartungsfenster planen.', text: 'Patch night zeitlich ändern und den Geltungsbereich prüfen.', steps: ['Patch night bearbeiten', '23:00–02:00 speichern', 'Geltungsbereich prüfen'], duration: '3 Minuten' },
  ],
  en: [
    { id: 'build', title: 'Build a workflow.', text: 'Connect three activities, publish the workflow and check your execution.', steps: ['Connect the activities', 'Publish the workflow', 'Check the result'], duration: '4 minutes' },
    { id: 'decision', title: 'Follow a decision.', text: 'With 8 GB free, follow the critical path of a disk-space check.', steps: ['Enter 8 GB', 'Run the critical path', 'Check the result'], duration: '3 minutes' },
    { id: 'parallel', title: 'Watch parallel work.', text: 'Checksum and cleanup both finish before the summary email.', steps: ['Start cleanup', 'Follow both branches', 'Check the junction'], duration: '3 minutes' },
    { id: 'service', title: 'Recover a service.', text: 'Start a stopped service and follow its verification.', steps: ['Start with a stopped service', 'Follow verification', 'Check the message'], duration: '3 minutes' },
    { id: 'live', title: 'Control a live run.', text: 'Find a running execution in Live Ops and cancel it.', steps: ['Start the execution', 'Open Live Ops', 'Confirm cancellation'], duration: '2 minutes' },
    { id: 'versions', title: 'Compare two versions.', text: 'Find the activity missing from the previous version.', steps: ['Open version comparison', 'Select the older version', 'Name the difference'], duration: '2 minutes' },
    { id: 'machine', title: 'Inspect a target machine.', text: 'Find LAB01 and assess its reachability and workflow usage.', steps: ['Filter for LAB01', 'Read status and usage', 'Assess the impact'], duration: '2 minutes' },
    { id: 'maintenance', title: 'Plan a maintenance window.', text: 'Change Patch night and check which workflows it covers.', steps: ['Edit Patch night', 'Save 23:00–02:00', 'Check its scope'], duration: '3 minutes' },
  ],
} as const satisfies Record<Lang, readonly { id: string; title: string; text: string; steps: readonly string[]; duration: string }[]>
