# Auditstand und nächster Einstieg

Stand: 12. Oktober 2026. Projekt: E:\NodePilot.
Aktueller Branch nach der beauftragten lokalen Übernahme: `main`.
Audit-Branch: `audit/practical-impact-2026-10-12`.
Basis: `de7105810af83cd21d20e32fc86bfbd3b4e280f4` auf lokalem `main`.

## Aktuelles Ergebnis

Die ausdrücklich angeforderte unabhängige Zusatzprüfung ist abgeschlossen.
Ein neuer Agent ohne Gesprächskontext und ohne Kenntnis früherer Auditberichte,
Fixlisten oder Prüfprotokolle hat beide Skills auf den aktuellen Arbeitsbaum
angewandt. Zwei ebenfalls kontextfreie Teilprüfer unterstützten die Quellprüfung.

**Diese Runde war keine Nullfundrunde:** Sie bestätigte fünf weitere Medium-Funde
(#143–#147), davon einen Sicherheitsbefund. Alle fünf wurden nach Gegenprüfung
behoben und gezielt getestet. Die anschließende Gegenprüfung der Integration und
weitere Quellprüfungen ergaben keine weiteren bestätigten Funde im beauftragten
Scope: Critical/High sowie Medium mit konkreter Alltagsauswirkung. Das ist ein
risikobasiertes Ergebnis, keine Zusicherung unbekannter Fehlerfreiheit.

Runde 5 hatte acht Korrekturen (#134–#141), Runde 6 eine weitere (#142). Das
damalige Nullfundergebnis der Runde 7 bleibt historisch nachvollziehbar, wurde
aber durch die zusätzlichen Funde dieser unabhängigen Runde ergänzt.

## Maßgebliche Unterlagen

1. [Unabhängiger Bericht](../independent-review-2026-10-12.md): Funde,
   Quellabdeckung und Grenzen, Gegenprüfung, vollständige Testbefehle.
2. [Alle 145 Korrekturen einfach erklärt](fix-list-de.md): mögliche Alltagsfolgen,
   keine behaupteten Vorfälle auf der Nutzerinstallation.
3. [Aktuelle Dateihashes](independent-state-2026-10-12.json): Produktcode,
   Tests, Deploymentskripte und betroffene Produktdokumentation.
4. Historische [Runde 5](round-5.md), [Runde 6](round-6.md),
   [Runde 7](round-7.md) und deren [Hashmanifest](final-state-2026-10-12.json).
5. [Ursprünglicher Review](../architecture-security-review-2026-10-09.md)
   und [früherer Pausenstand](pause-2026-10-09.md).

## Neue Korrekturen und Nachweise

- #143: Gespeicherte ausstehende Execution-/Audit-Alarme finden ihre erhaltenen
  Quellereignisse auch außerhalb des normalen Suchzeitraums.
- #144: Agenten halten WinRM-Verbindungsplätze nur während des Remote-Aufrufs.
- #145: Gemeinsame Alarmverarbeitung prüft vorhandene Zustellungen vor der
  Sperrzeit und zählt Ereignisse dauerhaft nur einmal. Neue Migration:
  `20261012070731_TrackNotificationOccurrences`. Bereinigung und Regellöschung
  entfernen die zugehörigen Einträge.
- #146: Desktop-Rollback stellt die ursprüngliche Verbindungsdatei samt ACL wieder her.
- #147: Server-Rollback schützt die Registry vor Wiederherstellung der
  Dienstumgebung auch bei SQL-Server-/Seed-Backup-Konfigurationen.

102 gezielte Engine-/Alert-/Pooltests und zwei Skillintegrationen bestanden;
anschließend 96 Alarm-/Bereinigungstests, zwölf Migrationsprüfungen und sechs
Regel-Lebenszyklustests. Die Testmengen überschneiden sich; ihre Summe ist keine
Anzahl einzigartiger Tests. Drei PowerShell-Prüfskripte bestanden.
RED-Nachweise existieren für #145–#147; für #143/#144 wurde kein RED-Lauf ausgeführt.

Die Logs für #145 liegen unter `E:/NodePilot-independent-145-*.log` (red, green,
drift, rule-lifecycle, migration, build). Die übrigen Nachweise sind in den
Toolausgaben und im unabhängigen Bericht dokumentiert. Der erste Debug-Build
scheiterte an gesperrten DLLs der laufenden Entwicklungs-API; Migrationserzeugung
mit separater Buildkonfiguration `Independent145` und Folgeprüfungen gelangen.

## Arbeitsbaum und Freigaben

- Die 14 neuen Korrekturen dieser Fortsetzung (#134–#147) wurden auf ausdrücklichen
  Nutzerauftrag lokal committet und per Fast-forward nach `main` übernommen.
  Commit-Titel: `Fix practical-impact audit findings and record independent review`.
  Kein Push, PR oder Deployment. Die Auditberichte und Hashmanifeste dokumentieren
  den Prüfstand vor dieser anschließenden lokalen Übernahme.
- Frühere Korrekturen wurden mit `318ae01` lokal committet und sind in der Basis.
- `src/nodepilot-docs-ui/src/site/image-manifest.json` war vorher geändert und
  wurde nicht bearbeitet. Nicht als Auditänderung übernehmen.
- Die neue Migration wurde weder auf die laufende Entwicklungsdatenbank noch
  auf CM1 angewandt. Bestehende Dienste und Entwicklungsprozesse wurden nicht
  neu gestartet. Die laufende Instanz repräsentiert daher weiterhin ihren zuvor
  gestarteten Stand.
- Lose Auditlogs und Buildartefakte nicht pauschal mitcommitten. Vor einer
  späteren Fortsetzung Branch, Arbeitsbaum und aktuelles Hashmanifest prüfen;
  nichts zurücksetzen oder aus alten Sicherungen überschreiben.

## Grenzen und weiterer Einstieg

Keine neue CM1-/Browser-/Installer-/WinRM-/HA-Abnahme oder echte
Providerdatenbankprüfung. Tests verwenden SQLite, lokale Runspaces und
simulierte Dienst-/Registry-Aufrufe. Keine vollständige Testsuite oder lückenlose
Zeilenprüfung behauptet; konkrete Abdeckungslücken stehen in der Berichtsmatrix.

Agent-Registries im portablen Backup sind dokumentiert ausgeschlossen und wurden
nicht als Defekt behandelt. Ein Verdacht zu großen Skriptausgabepuffern blieb
unbestätigt; Ausgabe wurde wegen des Databus-Vertrags nicht pauschal gekürzt.
Pure Edge Cases, Low und kosmetische Refactorings bleiben außerhalb des Auftrags.
#133 ist Low und nicht implementiert; #70 ist unbestätigt. Nächste freie ID: 148.

Skills: `E:/NodePilot/.agents/skills/improve-codebase-architecture/SKILL.md` und
`C:/Users/flori/.codex/skills/audit-ai-slop-code/SKILL.md`.
Root-/UI-CLAUDE, CONTEXT und passende ADRs gelten weiter. .NET-Builds wegen
gemeinsamer bin-/obj-Verzeichnisse seriell koordinieren. Ohne ausdrückliche
Anweisung keinen Commit, Push oder Deployment ableiten.
